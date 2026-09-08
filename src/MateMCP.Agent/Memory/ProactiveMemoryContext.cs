using System.Text;
using MateMCP.Agent.Audit;

namespace MateMCP.Agent.Memory;

public static class ProactiveMemoryContext
{
    public const int MaxItems = 3;
    public const int MaxChars = 3500;

    public static async Task<string?> BuildAsync(
        SkillMemoryStore store,
        AuditLog audit,
        string tool,
        string? project,
        string? query,
        CancellationToken cancellationToken = default)
    {
        var items = await store.ApplicableAsync(project, cancellationToken);
        if (items.Count == 0) return null;

        var terms = Tokenize(query);
        var ranked = items
            .Select(item => new { Item = item, Score = Score(item, terms) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.UpdatedAt)
            .Take(MaxItems)
            .Select(x => x.Item)
            .ToArray();
        if (ranked.Length == 0) return null;

        var builder = new StringBuilder();
        builder.AppendLine("MateMCP durable context (automatically supplied; current user instructions override persisted context):");
        foreach (var item in ranked)
        {
            var prefix = $"- [{item.Type}; {item.Scope}; id={item.Id}] {item.Title}: ";
            if (builder.Length + prefix.Length >= MaxChars) break;
            builder.Append(prefix);
            AppendBounded(builder, item.Content, MaxChars);
            builder.AppendLine();
            if (builder.Length >= MaxChars) break;
        }

        var result = builder.ToString().TrimEnd();
        await audit.WriteAsync(
            "memory.inject",
            string.IsNullOrWhiteSpace(project) ? tool : $"{tool}:{project}",
            $"items:{ranked.Length};chars:{result.Length}",
            cancellationToken);
        return result;
    }

    private static int Score(SkillMemoryItem item, IReadOnlyList<string> terms)
    {
        var score = string.Equals(item.Scope, "project", StringComparison.OrdinalIgnoreCase) ? 8 : 0;
        if (item.Type is "rule" or "skill" or "procedure") score += 3;

        foreach (var term in terms)
        {
            if (item.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (item.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))) score += 8;
            if (item.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) score += 5;
            if (item.Content.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 2;
        }

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
