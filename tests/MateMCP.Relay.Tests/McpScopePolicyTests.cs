using MateMCP.Relay;

namespace MateMCP.Relay.Tests;

public sealed class McpScopePolicyTests
{
    [Theory]
    [InlineData("shell_exec")]
    [InlineData("ssh_session_start")]
    [InlineData("ssh_session_authenticate")]
    [InlineData("shell_session_start")]
    [InlineData("shell_session_read")]
    [InlineData("shell_session_write")]
    [InlineData("shell_session_send_secret")]
    [InlineData("shell_session_close")]
    public void Shell_tools_require_shell_scope(string tool)
    {
        Assert.Equal("mcp:shell", McpScopePolicy.RequiredScopeForTool(tool));
    }

    [Fact]
    public void Filesystem_write_requires_write_scope()
    {
        Assert.Equal("mcp:write", McpScopePolicy.RequiredScopeForTool("filesystem_write"));
    }

    [Theory]
    [InlineData("filesystem_read")]
    [InlineData("secret_list")]
    [InlineData("unknown_tool")]
    [InlineData(null)]
    public void Read_only_or_unknown_tools_default_to_read_scope(string? tool)
    {
        Assert.Equal("mcp:read", McpScopePolicy.RequiredScopeForTool(tool));
    }
}
