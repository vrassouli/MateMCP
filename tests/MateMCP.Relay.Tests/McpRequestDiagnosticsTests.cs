using MateMCP.Relay;

namespace MateMCP.Relay.Tests;

public sealed class McpRequestDiagnosticsTests
{
    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/mcp/agt_test", "protected-resource-discovery")]
    [InlineData("/mcp/agt_test", "mcp-transport")]
    [InlineData("/health", "other")]
    public void Stage_classifies_public_interop_requests(string path, string expected)
        => Assert.Equal(expected, McpRequestDiagnostics.Stage(path));

    [Fact]
    public void Safe_header_removes_newlines_and_bounds_output()
    {
        var value = "application/json\r\nAuthorization: Bearer should-not-be-a-new-log-line" + new string('x', 300);
        var safe = McpRequestDiagnostics.SafeHeader(value);

        Assert.DoesNotContain('\r', safe);
        Assert.DoesNotContain('\n', safe);
        Assert.True(safe.Length <= 161);
    }

    [Fact]
    public void Safe_redirect_never_logs_query_or_fragment()
    {
        var safe = McpRequestDiagnostics.SafeRedirect("https://api.matemcp.com/connect/authorize?code=secret&state=secret#fragment");

        Assert.Equal("https://api.matemcp.com/connect/authorize", safe);
        Assert.DoesNotContain("secret", safe);
    }
}
