using System.Text.Json;
using MateMCP.Agent.Projects;

namespace MateMCP.Agent.Memory;

public sealed record SkillMemoryItem(
    string Id,
    string Title,
    string Type,
    string Scope,
    string? Project,
    IReadOnlyList<string> Tags,
    string? Description,
    string Content,
    string Source,
    string UpdatedBy,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SkillMemoryUpdate(
    string Title,
    string Type,
    string Scope,
    string? Project,
    IReadOnlyList<string>? Tags,
    string? Description,
    string Content,
    string Source = "user",
    bool Enabled = true);

public sealed class SkillMemoryStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProjectRegistry projects;
    private readonly string _path;

    public SkillMemoryStore(ProjectRegistry projects) : this(projects, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MateMCP", "skills-memory.json")) { }

    public SkillMemoryStore(ProjectRegistry projects, string path)
    {
        this.projects = projects;
        _path = path;
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<SkillMemoryItem>> SearchAsync(string? scope = null, string? project = null, string? type = null,
        string? text = null, bool includeDisabled = false, CancellationToken cancellationToken = default)
    {
        var normalizedScope = NormalizeScope(scope, allowNull: true);
        ProjectDefinition? projectFilter = null;
        if (!string.IsNullOrWhiteSpace(project)) projectFilter = projects.Get(project.Trim());
        if (string.Equals(normalizedScope, "global", StringComparison.OrdinalIgnoreCase) && projectFilter is not null)
            throw new ArgumentException("Global searches cannot specify a project.");

        var query = (await LoadAsync(cancellationToken)).AsEnumerable();
        if (!includeDisabled) query = query.Where(x => x.Enabled);
        if (normalizedScope is not null) query = query.Where(x => string.Equals(x.Scope, normalizedScope, StringComparison.OrdinalIgnoreCase));
        if (projectFilter is not null) query = query.Where(x => MatchesProject(x.Project, projectFilter));
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => string.Equals(x.Type, type.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(text))
        {
            var q = text.Trim();
            query = query.Where(x => x.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (x.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || x.Content.Contains(q, StringComparison.OrdinalIgnoreCase)
                || x.Tags.Any(tag => tag.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }
        return query.OrderByDescending(x => x.UpdatedAt).ToArray();
    }

    public async Task<IReadOnlyList<SkillMemoryItem>> ApplicableAsync(string? project, CancellationToken cancellationToken = default)
    {
        var configuredProject = string.IsNullOrWhiteSpace(project) ? null : projects.Get(project.Trim());
        var items = await LoadAsync(cancellationToken);
        return items.Where(x => x.Enabled && (string.Equals(x.Scope, "global", StringComparison.OrdinalIgnoreCase)
            || (configuredProject is not null && string.Equals(x.Scope, "project", StringComparison.OrdinalIgnoreCase)
                && MatchesProject(x.Project, configuredProject))))
            .OrderBy(x => string.Equals(x.Scope, "project", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(x => x.UpdatedAt).ToArray();
    }

    public async Task<SkillMemoryItem> GetAsync(string id, CancellationToken cancellationToken = default)
        => (await LoadAsync(cancellationToken)).SingleOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? throw new KeyNotFoundException($"Skills/Memory item '{id}' was not found.");

    public Task<SkillMemoryItem> CreateAsync(SkillMemoryUpdate update, CancellationToken cancellationToken = default)
        => SaveAsync(null, update, cancellationToken);

    public Task<SkillMemoryItem> UpdateAsync(string id, SkillMemoryUpdate update, CancellationToken cancellationToken = default)
        => SaveAsync(id, update, cancellationToken);

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadUnsafeAsync(cancellationToken);
            var removed = items.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) await PersistUnsafeAsync(items, cancellationToken);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private async Task<SkillMemoryItem> SaveAsync(string? id, SkillMemoryUpdate update, CancellationToken cancellationToken)
    {
        ValidateContent(update);
        var scope = NormalizeScope(update.Scope, allowNull: false)!;
        var project = NormalizeProject(update.Project, scope);
        var source = string.IsNullOrWhiteSpace(update.Source) ? "user" : update.Source.Trim().ToLowerInvariant();
        if (source is not ("user" or "ai" or "import")) throw new ArgumentException("Source must be user, ai, or import.");
        var tags = (update.Tags ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadUnsafeAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (id is null)
            {
                var item = new SkillMemoryItem(Guid.NewGuid().ToString("N"), update.Title.Trim(), update.Type.Trim().ToLowerInvariant(), scope,
                    project, tags, Clean(update.Description), update.Content.Trim(), source, source, update.Enabled, now, now);
                items.Add(item);
                await PersistUnsafeAsync(items, cancellationToken);
                return item;
            }

            var index = items.FindIndex(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new KeyNotFoundException($"Skills/Memory item '{id}' was not found.");
            var existing = items[index];
            var updated = existing with
            {
                Title = update.Title.Trim(), Type = update.Type.Trim().ToLowerInvariant(), Scope = scope, Project = project, Tags = tags,
                Description = Clean(update.Description), Content = update.Content.Trim(), UpdatedBy = source, Enabled = update.Enabled, UpdatedAt = now
            };
            items[index] = updated;
            await PersistUnsafeAsync(items, cancellationToken);
            return updated;
        }
        finally { _gate.Release(); }
    }

    private string? NormalizeProject(string? project, string? scope)
    {
        if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Global items cannot specify a project.");
            return null;
        }
        if (string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Project-scoped items require a configured project name or id.");
            return projects.Get(project.Trim()).Id;
        }
        if (!string.IsNullOrWhiteSpace(project)) return projects.Get(project.Trim()).Id;
        return null;
    }

    private static bool MatchesProject(string? storedReference, ProjectDefinition project)
        => string.Equals(storedReference, project.Id, StringComparison.OrdinalIgnoreCase)
           || string.Equals(storedReference, project.Name, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeScope(string? scope, bool allowNull)
    {
        if (string.IsNullOrWhiteSpace(scope)) return allowNull ? null : throw new ArgumentException("Scope must be global or project.");
        var value = scope.Trim().ToLowerInvariant();
        return value is "global" or "project" ? value : throw new ArgumentException("Scope must be global or project.");
    }

    private static void ValidateContent(SkillMemoryUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.Title)) throw new ArgumentException("Title is required.");
        if (string.IsNullOrWhiteSpace(update.Type)) throw new ArgumentException("Type is required.");
        if (string.IsNullOrWhiteSpace(update.Content)) throw new ArgumentException("Content is required.");
        if (update.Title.Length > 200) throw new ArgumentException("Title is too long.");
        if (update.Content.Length > 250_000) throw new ArgumentException("Content is too long.");
        var lower = update.Content.ToLowerInvariant();
        if (lower.Contains("password=") || lower.Contains("api_key=") || lower.Contains("api-key:") || lower.Contains("bearer "))
            throw new ArgumentException("Skills/Memory is not a secret store. Store credentials in MateMCP Secret Management instead.");
    }

    private async Task<List<SkillMemoryItem>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await LoadUnsafeAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<List<SkillMemoryItem>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<SkillMemoryItem>>(stream, Json, cancellationToken) ?? [];
    }

    private async Task PersistUnsafeAsync(List<SkillMemoryItem> items, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await JsonSerializer.SerializeAsync(stream, items, Json, cancellationToken);
            File.Move(temp, _path, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
