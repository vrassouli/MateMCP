namespace MateMCP.Relay;

public static class McpScopePolicy
{
    public static string RequiredScopeForTool(string? toolName) => toolName switch
    {
        "filesystem_write" => "mcp:write",
        "shell_exec" => "mcp:shell",
        "ssh_session_start" => "mcp:shell",
        "ssh_session_authenticate" => "mcp:shell",
        "shell_session_start" => "mcp:shell",
        "shell_session_read" => "mcp:shell",
        "shell_session_write" => "mcp:shell",
        "shell_session_send_secret" => "mcp:shell",
        "shell_session_close" => "mcp:shell",
        _ => "mcp:read"
    };
}
