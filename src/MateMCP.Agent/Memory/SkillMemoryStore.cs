using System.Text;
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
    DateTimeOffset UpdatedAt,
    bool Archived = false);

public sealed record SkillMemoryUpdate(
    string Title,
    string Type,
    string Scope,
    string? Project,
    IReadOnlyList<string>? Tags,
    string? Description,
    string Content,
    string Source = "user",
    bool Enabled = true,
    bool Archived = false);

public sealed record LegacyProjectMemoryMigrationResult(
    int Found,
    int MigratedToRepository,
    int ArchivedOnly,
    string? ArchivePath,
    IReadOnlyList<string> RepositoryFiles);

public sealed class SkillMemoryStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProjectRegistry projects;
    private readonly string _path;
    private bool _legacyMigrationChecked;

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
        ValidateGlobalQuery(scope, project);
        var query = (await LoadAsync(cancellationToken)).AsEnumerable();
        if (!includeDisabled) query = query.Where(x => x.Enabled);
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

    // The project parameter is intentionally retained for source/API compatibility with older callers.
    // Project-specific durable context now lives in repository SKILL.md files; only global items are returned here.
    public async Task<IReadOnlyList<SkillMemoryItem>> ApplicableAsync(string? project = null, CancellationToken cancellationToken = default)
    {
        var items = await LoadAsync(cancellationToken);
        return items.Where(x => x.Enabled && !x.Archived)
            .OrderByDescending(x => x.UpdatedAt)
            .ToArray();
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

    public async Task<LegacyProjectMemoryMigrationResult> MigrateLegacyProjectItemsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await MigrateLegacyProjectItemsUnsafeAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<SkillMemoryItem> SaveAsync(string? id, SkillMemoryUpdate update, CancellationToken cancellationToken)
    {
        ValidateContent(update);
        ValidateGlobalUpdate(update);
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
                var item = new SkillMemoryItem(Guid.NewGuid().ToString("N"), update.Title.Trim(), update.Type.Trim().ToLowerInvariant(), "global",
                    null, tags, Clean(update.Description), update.Content.Trim(), source, source, update.Enabled, now, now, update.Archived);
                items.Add(item);
                await PersistUnsafeAsync(items, cancellationToken);
                return item;
            }

            var index = items.FindIndex(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new KeyNotFoundException($"Skills/Memory item '{id}' was not found.");
            var existing = items[index];
            var updated = existing with
            {
                Title = update.Title.Trim(), Type = update.Type.Trim().ToLowerInvariant(), Scope = "global", Project = null, Tags = tags,
                Description = Clean(update.Description), Content = update.Content.Trim(), UpdatedBy = source, Enabled = update.Enabled,
                Archived = update.Archived, UpdatedAt = now
            };
            items[index] = updated;
            await PersistUnsafeAsync(items, cancellationToken);
            return updated;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateGlobalQuery(string? scope, string? project)
    {
        if (!string.IsNullOrWhiteSpace(project))
            throw new ArgumentException("Project-specific knowledge is stored in repository SKILL.md files, not MateMCP global Skills & Memory.");
        if (!string.IsNullOrWhiteSpace(scope) && !string.Equals(scope.Trim(), "global", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Skills & Memory now supports global scope only. Store project-specific knowledge in the project's .matemcp/skills directory.");
    }

    private static void ValidateGlobalUpdate(SkillMemoryUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.Project))
            throw new ArgumentException("Global Skills & Memory items cannot specify a project. Store project-specific knowledge in repository SKILL.md files.");
        if (!string.IsNullOrWhiteSpace(update.Scope) && !string.Equals(update.Scope.Trim(), "global", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Skills & Memory now supports global scope only. Store project-specific knowledge in the project's .matemcp/skills directory.");
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
        if (!_legacyMigrationChecked)
            await MigrateLegacyProjectItemsUnsafeAsync(cancellationToken);

        var items = await ReadUnsafeAsync(cancellationToken);
        return items.Where(IsGlobal).ToList();
    }

    private async Task<LegacyProjectMemoryMigrationResult> MigrateLegacyProjectItemsUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_legacyMigrationChecked)
            return new LegacyProjectMemoryMigrationResult(0, 0, 0, null, Array.Empty<string>());

        var items = await ReadUnsafeAsync(cancellationToken);
        var legacyItems = items.Where(x => !IsGlobal(x)).ToArray();
        if (legacyItems.Length == 0)
        {
            _legacyMigrationChecked = true;
            return new LegacyProjectMemoryMigrationResult(0, 0, 0, null, Array.Empty<string>());
        }

        var archivePath = await ArchiveLegacyProjectItemsAsync(legacyItems, cancellationToken);
        var repositoryFiles = new List<string>();
        var migrated = 0;

        foreach (var item in legacyItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Enabled || item.Archived) continue;
            var project = ResolveProject(item.Project);
            if (project is null || !project.Available || !project.Write) continue;

            try
            {
                var relative = BuildRepositorySkillPath(item);
                var absolute = Path.Combine(project.Root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                if (!File.Exists(absolute))
                    await File.WriteAllTextAsync(absolute, BuildRepositorySkill(item), new UTF8Encoding(false), cancellationToken);
                migrated++;
                repositoryFiles.Add($"{project.Name}:{relative}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // The archive remains the lossless fallback for projects that cannot be written right now.
            }
        }

        await PersistUnsafeAsync(items.Where(IsGlobal).ToList(), cancellationToken);
        _legacyMigrationChecked = true;
        return new LegacyProjectMemoryMigrationResult(
            legacyItems.Length,
            migrated,
            legacyItems.Length - migrated,
            archivePath,
            repositoryFiles);
    }

    private async Task<string> ArchiveLegacyProjectItemsAsync(IReadOnlyCollection<SkillMemoryItem> legacyItems, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var archivePath = Path.Combine(directory, "skills-memory.project-archive.json");
        var archived = new List<SkillMemoryItem>();
        if (File.Exists(archivePath))
        {
            try
            {
                await using var existing = File.OpenRead(archivePath);
                archived = await JsonSerializer.DeserializeAsync<List<SkillMemoryItem>>(existing, Json, cancellationToken) ?? [];
            }
            catch (JsonException) { }
        }

        foreach (var item in legacyItems)
        {
            var index = archived.FindIndex(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) archived[index] = item;
            else archived.Add(item);
        }

        var temp = archivePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await JsonSerializer.SerializeAsync(stream, archived, Json, cancellationToken);
            File.Move(temp, archivePath, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        return archivePath;
    }

    private ProjectDefinition? ResolveProject(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        return projects.All.FirstOrDefault(x => string.Equals(x.Id, reference, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.Name, reference, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildRepositorySkillPath(SkillMemoryItem item)
    {
        var slug = Slugify(item.Title);
        var id = item.Id.Length <= 8 ? item.Id : item.Id[..8];
        return $".matemcp/skills/{slug}-{id}/SKILL.md";
    }

    private static string BuildRepositorySkill(SkillMemoryItem item)
    {
        var required = string.Equals(item.Type, "rule", StringComparison.OrdinalIgnoreCase)
            || item.Tags.Any(x => x.Equals("always", StringComparison.OrdinalIgnoreCase) || x.Equals("required", StringComparison.OrdinalIgnoreCase));
        var description = Clean(item.Description) ?? $"Migrated MateMCP project {item.Type}.";
        var triggers = item.Tags.Where(x => !string.Equals(x, "always", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(x, "required", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"name: \"{YamlScalar(item.Title)}\"");
        builder.AppendLine($"description: \"{YamlScalar(description)}\"");
        if (triggers.Length > 0) builder.AppendLine($"triggers: {string.Join(", ", triggers.Select(x => $"\"{YamlScalar(x)}\""))}");
        if (required) builder.AppendLine("mode: required");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine($"# {item.Title.Trim().Replace('\r', ' ').Replace('\n', ' ')}");
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            builder.AppendLine(item.Description.Trim());
            builder.AppendLine();
        }
        builder.AppendLine(item.Content.Trim());
        builder.AppendLine();
        builder.AppendLine($"<!-- Migrated from MateMCP project-scoped Skills & Memory item {item.Id}. -->");
        return builder.ToString();
    }

    private static string YamlScalar(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Slugify(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            if (builder.Length >= 48) break;
        }
        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "project-memory" : slug;
    }

    private async Task<List<SkillMemoryItem>> ReadUnsafeAsync(CancellationToken cancellationToken)
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

    private static bool IsGlobal(SkillMemoryItem item)
        => string.Equals(item.Scope, "global", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(item.Project);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
