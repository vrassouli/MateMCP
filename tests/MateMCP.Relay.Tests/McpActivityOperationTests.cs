using System.Text;
using MateMCP.Relay;

namespace MateMCP.Relay.Tests;

public sealed class McpActivityOperationTests
{
    [Fact]
    public void Tool_call_exposes_only_method_and_tool_name()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"shell_exec","arguments":{"command":"echo SUPER-SECRET","password":"hunter2"}}}
            """);

        var operation = McpActivityOperation.Describe("POST", payload);

        Assert.Equal("tools/call · shell_exec", operation);
        Assert.DoesNotContain("SUPER-SECRET", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("arguments", operation, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_tool_method_reports_method_without_params()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"file:///secret/path"}}
            """);

        Assert.Equal("resources/read", McpActivityOperation.Describe("POST", payload));
    }

    [Fact]
    public void Invalid_or_batch_payload_uses_safe_generic_description()
    {
        Assert.Equal("MCP POST", McpActivityOperation.Describe("POST", Encoding.UTF8.GetBytes("not-json")));
        Assert.Equal("MCP batch", McpActivityOperation.Describe("POST", Encoding.UTF8.GetBytes("[]")));
    }
}
