namespace MateMCP.Relay;

public static class McpScopePolicy
{
    public const string UnsupportedScope = "mcp:unsupported";

    public static string RequiredScopeForTool(string? toolName) => toolName switch
    {
        "filesystem_projects" => "mcp:read",
        "filesystem_list" => "mcp:read",
        "filesystem_read" => "mcp:read",
        "secret_list" => "mcp:read",
        "filesystem_write" => "mcp:write",
        "shell_exec" => "mcp:shell",
        "shell_session_start" => "mcp:shell",
        "shell_session_read" => "mcp:shell",
        "shell_session_write" => "mcp:shell",
        "shell_session_send_secret" => "mcp:shell",
        "shell_session_close" => "mcp:shell",
        _ => UnsupportedScope
    };
}
