using System.Text;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;

namespace MateMCP.Agent.Memory;

public static class ProactiveMemoryContext
{
    public const int MaxItems = 3;
    public const int MaxChars = 3500;

    public static Task<string?> BuildAsync(
        SkillMemoryStore store,
        AuditLog audit,
        string tool,
        string? project,
        string? query,
        CancellationToken cancellationToken = default)
        => BuildAsync(store, audit, tool, project, query, new ProactiveMemoryOptions(), cancellationToken);

    public static async Task<string?> BuildAsync(
        SkillMemoryStore store,
        AuditLog audit,
        string tool,
        string? project,
        string? query,
        ProactiveMemoryOptions options,
        CancellationToken cancellationToken = default)
    {
        var mode = options?.Mode ?? ProactiveMemoryMode.Automatic;
        if (mode == ProactiveMemoryMode.Off) return null;

        var items = await store.ApplicableAsync(project, cancellationToken);
        if (items.Count == 0) return null;

        var terms = Tokenize(query);
        var maxItems = Math.Clamp(options?.MaxItems ?? MaxItems, 1, 8);
        var maxChars = Math.Clamp(options?.MaxChars ?? MaxChars, 512, 12_000);
        var ranked = items
            .Select(item => new { Item = item, Score = Score(item, terms) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.UpdatedAt)
            .Take(maxItems)
            .Select(x => x.Item)
            .ToArray();
        if (ranked.Length == 0) return null;

        var typeSummary = string.Join(',', ranked
            .GroupBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key}:{x.Count()}"));

        if (mode == ProactiveMemoryMode.Suggested)
        {
            await audit.WriteAsync(
                "memory.suggest",
                string.IsNullOrWhiteSpace(project) ? tool : $"{tool}:{project}",
                $"items:{ranked.Length};types:{typeSummary}",
                cancellationToken);
            return $"MateMCP found {ranked.Length} relevant durable context item(s) ({typeSummary}). Consult Skills & Memory before continuing if the task depends on prior project decisions or procedures.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("MateMCP durable context (automatically supplied; current user instructions override persisted context):");
        foreach (var item in ranked)
        {
            var prefix = $"- [{item.Type}; {item.Scope}; id={item.Id}] {item.Title}: ";
            if (builder.Length + prefix.Length >= maxChars) break;
            builder.Append(prefix);
            AppendBounded(builder, item.Content, maxChars);
            builder.AppendLine();
            if (builder.Length >= maxChars) break;
        }

        var result = builder.ToString().TrimEnd();
        await audit.WriteAsync(
            "memory.inject",
            string.IsNullOrWhiteSpace(project) ? tool : $"{tool}:{project}",
            $"items:{ranked.Length};chars:{result.Length};types:{typeSummary}",
            cancellationToken);
        return result;
    }

    private static int Score(SkillMemoryItem item, IReadOnlyList<string> terms)
    {
        var projectScoped = string.Equals(item.Scope, "project", StringComparison.OrdinalIgnoreCase);
        var alwaysActive = string.Equals(item.Type, "rule", StringComparison.OrdinalIgnoreCase)
            || item.Tags.Any(tag => tag.Equals("always", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("required", StringComparison.OrdinalIgnoreCase));
        var score = projectScoped ? 4 : 0;
        var matched = false;

        foreach (var term in terms)
        {
            if (item.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) { score += 10; matched = true; }
            if (item.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase)
                || term.Contains(tag, StringComparison.OrdinalIgnoreCase))) { score += 8; matched = true; }
            if (item.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) { score += 5; matched = true; }
            if (item.Content.Contains(term, StringComparison.OrdinalIgnoreCase)) { score += 2; matched = true; }
        }

        if (alwaysActive) return score + 20;
        if (!matched) return 0;
        if (item.Type is "skill" or "procedure") score += 3;
        return score;
    }

    private static IReadOnlyList<string> Tokenize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        return query
            .Split([' ', '\t', '\r', '\n', '/', '\\', ':', ';', ',', '.', '-', '_', '=', '(', ')', '[', ']', '{', '}'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
    }

    private static void AppendBounded(StringBuilder builder, string content, int maxChars)
    {
        var normalized = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var available = maxChars - builder.Length;
        if (available <= 0) return;
        if (normalized.Length <= available)
        {
            builder.Append(normalized);
            return;
        }

        if (available <= 1) return;
        builder.Append(normalized.AsSpan(0, available - 1));
        builder.Append('…');
    }
}
