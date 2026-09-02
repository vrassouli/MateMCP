using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class McpToolCatalogTests
{
    [Fact]
    public void Catalog_exposes_only_generic_shell_session_tools()
    {
        var expectedShellTools = new[]
        {
            "shell_exec",
            "shell_session_start",
            "shell_session_read",
            "shell_session_write",
            "shell_session_send_secret",
            "shell_session_close",
            "secret_list"
        };

        foreach (var tool in expectedShellTools)
            Assert.Contains(tool, McpToolCatalog.Names);

        Assert.DoesNotContain(McpToolCatalog.Names, name => name.StartsWith("ssh_", StringComparison.Ordinal));
        Assert.Equal(McpToolCatalog.Names.Count, McpToolCatalog.Names.Distinct(StringComparer.Ordinal).Count());
        Assert.Matches("^[0-9a-f]{16}$", McpToolCatalog.Revision);
    }
}
