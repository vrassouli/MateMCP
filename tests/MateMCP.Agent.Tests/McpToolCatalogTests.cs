using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class McpToolCatalogTests
{
    [Fact]
    public void Catalog_includes_structured_ssh_and_secret_tools()
    {
        Assert.Contains("ssh_session_start", McpToolCatalog.Names);
        Assert.Contains("ssh_session_authenticate", McpToolCatalog.Names);
        Assert.Contains("shell_session_send_secret", McpToolCatalog.Names);
        Assert.Equal(McpToolCatalog.Names.Count, McpToolCatalog.Names.Distinct(StringComparer.Ordinal).Count());
        Assert.Matches("^[0-9a-f]{16}$", McpToolCatalog.Revision);
    }
}
