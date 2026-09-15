using MateMCP.Relay;

namespace MateMCP.Relay.Tests;

public sealed class McpScopePolicyTests
{
    [Theory]
    [InlineData("shell_exec")]
    [InlineData("shell_session_start")]
    [InlineData("shell_session_read")]
    [InlineData("shell_session_write")]
    [InlineData("shell_session_send_secret")]
    [InlineData("shell_session_close")]
    public void Generic_shell_tools_require_shell_scope(string tool)
    {
        Assert.Equal("mcp:shell", McpScopePolicy.RequiredScopeForTool(tool));
    }

    [Theory]
    [InlineData("filesystem_write")]
    [InlineData("agent_file_upload_start")]
    [InlineData("agent_file_upload_chunk")]
    [InlineData("agent_file_upload_complete")]
    [InlineData("agent_file_upload_cancel")]
    public void Write_tools_require_write_scope(string tool)
    {
        Assert.Equal("mcp:write", McpScopePolicy.RequiredScopeForTool(tool));
    }

    [Theory]
    [InlineData("filesystem_projects")]
    [InlineData("filesystem_list")]
    [InlineData("filesystem_read")]
    [InlineData("secret_list")]
    public void Known_read_tools_require_read_scope(string tool)
    {
        Assert.Equal("mcp:read", McpScopePolicy.RequiredScopeForTool(tool));
    }

    [Theory]
    [InlineData("unknown_tool")]
    [InlineData("ssh_session_start")]
    [InlineData("ssh_session_authenticate")]
    [InlineData(null)]
    public void Unknown_or_removed_tools_fail_closed(string? tool)
    {
        Assert.Equal(McpScopePolicy.UnsupportedScope, McpScopePolicy.RequiredScopeForTool(tool));
    }
}
