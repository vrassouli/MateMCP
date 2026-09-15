using MateMCP.Agent.Tools;
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
    [InlineData("mouse_click")]
    [InlineData("keyboard_type")]
    [InlineData("window_focus")]
    [InlineData("ui_click")]
    [InlineData("browser_open")]
    [InlineData("browser_click")]
    public void Host_control_tools_require_shell_scope(string tool)
    {
        Assert.Equal("mcp:shell", McpScopePolicy.RequiredScopeForTool(tool));
    }

    [Theory]
    [InlineData("filesystem_write")]
    [InlineData("agent_file_upload_start")]
    [InlineData("agent_file_upload_chunk")]
    [InlineData("agent_file_upload_complete")]
    [InlineData("agent_file_upload_cancel")]
    [InlineData("project_register")]
    [InlineData("project_update")]
    [InlineData("project_unregister")]
    [InlineData("memory_create")]
    [InlineData("memory_update")]
    [InlineData("memory_delete")]
    public void Write_tools_require_write_scope(string tool)
    {
        Assert.Equal("mcp:write", McpScopePolicy.RequiredScopeForTool(tool));
    }

    [Theory]
    [InlineData("filesystem_projects")]
    [InlineData("filesystem_list")]
    [InlineData("filesystem_read")]
    [InlineData("secret_list")]
    [InlineData("agent_file_upload_status")]
    [InlineData("project_list")]
    [InlineData("project_get")]
    [InlineData("project_resolve")]
    [InlineData("memory_search")]
    [InlineData("memory_applicable")]
    [InlineData("memory_read")]
    [InlineData("screen_list")]
    [InlineData("window_list")]
    [InlineData("screen_capture")]
    [InlineData("ui_snapshot")]
    [InlineData("browser_wait_for")]
    [InlineData("browser_snapshot")]
    [InlineData("browser_screenshot")]
    [InlineData("browser_diagnostics")]
    [InlineData("visual_viewports")]
    [InlineData("visual_capture")]
    [InlineData("visual_compare")]
    public void Read_only_tools_require_read_scope(string tool)
    {
        Assert.Equal("mcp:read", McpScopePolicy.RequiredScopeForTool(tool));
    }

    [Fact]
    public void Every_published_agent_tool_has_explicit_relay_scope_classification()
    {
        var unclassified = McpToolCatalog.Names
            .Where(name => McpScopePolicy.RequiredScopeForTool(name) == McpScopePolicy.UnsupportedScope)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unclassified.Length == 0,
            $"Published Agent MCP tools are missing Relay OAuth scope classification: {string.Join(", ", unclassified)}");
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
