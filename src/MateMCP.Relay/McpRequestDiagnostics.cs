namespace MateMCP.Relay;

public static class McpRequestDiagnostics
{
    private const int MaxHeaderLength = 160;

    public static string Stage(string path)
    {
        if (path.StartsWith("/.well-known/oauth-protected-resource", StringComparison.OrdinalIgnoreCase))
            return "protected-resource-discovery";
        if (path.StartsWith("/mcp/", StringComparison.OrdinalIgnoreCase))
            return "mcp-transport";
        return "other";
    }

    public static string SafeHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= MaxHeaderLength ? normalized : normalized[..MaxHeaderLength] + "…";
    }

    public static string SafeRedirect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "invalid-uri";
        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }
}
