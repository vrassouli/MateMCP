using System.Text;
using MateMCP.Agent.Projects;

namespace MateMCP.Agent.Context;

public sealed record ProjectSkillContextResult(
    string? Context,
    IReadOnlyList<string> Sources,
    int MatchedCount);

public static class ProjectSkillContext
{
    public const int MaxSkills = 3;
    public const int MaxChars = 6_000;
    private const int MaxDiscoveredFiles = 128;
    private const int MinimumMatchScore = 5;

    private static readonly string[] SkillRoots =
    [
        ".matemcp/skills",
        ".agents/skills",
        ".agent/skills",
        "skills"
    ];

    public static ProjectSkillContextResult Build(
        ProjectDefinition project,
        string? query,
        string? relativePath)
    {
        if (!project.Read || !project.Available)
            return new ProjectSkillContextResult(null, Array.Empty<string>(), 0);

        var documents = Discover(project.Root);
        if (documents.Count == 0)
            return new ProjectSkillContextResult(null, Array.Empty<string>(), 0);

        var terms = Tokenize(string.Join(' ', new[] { query, relativePath }.Where(x => !string.IsNullOrWhiteSpace(x))));
        var ranked = documents
            .Select(document => new { Document = document, Score = Score(document, terms) })
            .Where(x => x.Document.Required || x.Score >= MinimumMatchScore)
            .OrderByDescending(x => x.Document.Required)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSkills)
            .Select(x => x.Document)
            .ToArray();

        if (ranked.Length == 0)
            return new ProjectSkillContextResult(null, Array.Empty<string>(), 0);

        var builder = new StringBuilder();
        builder.AppendLine("MateMCP project Skills (task-matched; current user instructions override persisted skill guidance):");
        foreach (var skill in ranked)
        {
            if (builder.Length >= MaxChars) break;
            var header = $"\nSkill: {skill.Title} [{skill.RelativePath}]";
            if (builder.Length + header.Length >= MaxChars) break;
            builder.AppendLine(header);
            if (!string.IsNullOrWhiteSpace(skill.Description))
                AppendBounded(builder, $"Description: {skill.Description}\n", MaxChars);
            AppendBounded(builder, skill.Body.Trim(), MaxChars);
            if (builder.Length < MaxChars) builder.AppendLine();
        }

        return new ProjectSkillContextResult(
            builder.ToString().TrimEnd(),
            ranked.Select(x => x.RelativePath).ToArray(),
            ranked.Length);
    }

    private static IReadOnlyList<SkillDocument> Discover(string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot);
        var paths = new HashSet<string>(PathComparer);

        var rootSkill = Path.Combine(root, "SKILL.md");
        if (File.Exists(rootSkill)) paths.Add(rootSkill);

        foreach (var relativeRoot in SkillRoots)
        {
            var skillRoot = Path.Combine(root, relativeRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(skillRoot)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(skillRoot, "SKILL.md", SearchOption.AllDirectories))
                {
                    paths.Add(path);
                    if (paths.Count >= MaxDiscoveredFiles) break;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            if (paths.Count >= MaxDiscoveredFiles) break;
        }

        return paths
            .OrderBy(x => x, PathComparer)
            .Take(MaxDiscoveredFiles)
            .Select(path => Parse(root, path))
            .Where(x => x is not null)
            .Cast<SkillDocument>()
            .ToArray();
    }

    private static SkillDocument? Parse(string root, string path)
    {
        string content;
        try { content = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (string.IsNullOrWhiteSpace(content)) return null;

        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = content;
        if (content.StartsWith("---", StringComparison.Ordinal))
        {
            var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
            var closing = normalized.IndexOf("\n---\n", 3, StringComparison.Ordinal);
            if (closing > 0)
            {
                foreach (var line in normalized[3..closing].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var separator = line.IndexOf(':');
                    if (separator <= 0) continue;
                    metadata[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"', '\'');
                }
                body = normalized[(closing + 5)..];
            }
        }

        var title = GetMetadata(metadata, "name", "title") ?? FirstHeading(body)
            ?? new DirectoryInfo(Path.GetDirectoryName(path) ?? root).Name;
        var description = GetMetadata(metadata, "description") ?? FirstParagraph(body);
        var triggerText = GetMetadata(metadata, "triggers", "tags", "keywords") ?? string.Empty;
        var triggers = SplitMetadataList(triggerText)
            .Concat(Tokenize(title))
            .Concat(Tokenize(relative.Replace("SKILL.md", string.Empty, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        var mode = GetMetadata(metadata, "mode", "activation") ?? string.Empty;
        var required = mode.Equals("required", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("always", StringComparison.OrdinalIgnoreCase);

        return new SkillDocument(relative, title, description, triggers, body, required);
    }

    private static int Score(SkillDocument skill, IReadOnlyCollection<string> terms)
    {
        if (terms.Count == 0) return 0;
        var score = 0;
        foreach (var term in terms)
        {
            if (skill.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 12;
            if (skill.Triggers.Any(trigger => trigger.Contains(term, StringComparison.OrdinalIgnoreCase)
                || term.Contains(trigger, StringComparison.OrdinalIgnoreCase))) score += 10;
            if (skill.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) score += 6;
            if (skill.RelativePath.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 5;
            if (skill.Body.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 1;
        }
        return score;
    }

    private static string? GetMetadata(IReadOnlyDictionary<string, string> metadata, params string[] names)
    {
        foreach (var name in names)
            if (metadata.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }

    private static IReadOnlyList<string> SplitMetadataList(string value)
        => value.Trim().Trim('[', ']')
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim('"', '\''))
            .Where(x => x.Length >= 2)
            .ToArray();

    private static string? FirstHeading(string body)
        => body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim();

    private static string? FirstParagraph(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (paragraph.Count > 0) break;
                continue;
            }
            if (line.StartsWith('#') || line.StartsWith("```", StringComparison.Ordinal)) continue;
            paragraph.Add(line);
            if (string.Join(' ', paragraph).Length >= 240) break;
        }
        return paragraph.Count == 0 ? null : string.Join(' ', paragraph);
    }

    private static IReadOnlyList<string> Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        return value
            .Split([' ', '\t', '\r', '\n', '/', '\\', ':', ';', ',', '.', '-', '_', '=', '(', ')', '[', ']', '{', '}'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(48)
            .ToArray();
    }

    private static void AppendBounded(StringBuilder builder, string value, int maxChars)
    {
        var remaining = maxChars - builder.Length;
        if (remaining <= 0) return;
        if (value.Length <= remaining)
        {
            builder.Append(value);
            return;
        }
        if (remaining <= 1) return;
        builder.Append(value.AsSpan(0, remaining - 1));
        builder.Append('…');
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record SkillDocument(
        string RelativePath,
        string Title,
        string? Description,
        IReadOnlyList<string> Triggers,
        string Body,
        bool Required);
}
