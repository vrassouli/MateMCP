using System.Text;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Relay;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class RelayActivityProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matemcp-activity-project-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Project_metadata_uses_only_the_canonical_configured_project_name()
    {
        var projects = CreateRegistry();
        var payload = """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"shell_exec","arguments":{"project":"proj-matemcp","command":"echo SUPER-SECRET","password":"hunter2"}}}
            """;

        var project = RelayConnector.ResolveActivityProject(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)),
            projects);

        Assert.Equal("MateMCP", project);
        Assert.DoesNotContain("SUPER-SECRET", project, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_or_non_tool_project_values_are_not_emitted_as_activity_metadata()
    {
        var projects = CreateRegistry();
        var unknown = """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"shell_exec","arguments":{"project":"TOP-SECRET"}}}
            """;
        var nonTool = """
            {"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"project":"MateMCP"}}
            """;

        Assert.Null(RelayConnector.ResolveActivityProject(Convert.ToBase64String(Encoding.UTF8.GetBytes(unknown)), projects));
        Assert.Null(RelayConnector.ResolveActivityProject(Convert.ToBase64String(Encoding.UTF8.GetBytes(nonTool)), projects));
        Assert.Null(RelayConnector.ResolveActivityProject("not-base64", projects));
    }

    private ProjectRegistry CreateRegistry()
    {
        Directory.CreateDirectory(_root);
        var options = new MateOptions
        {
            Projects =
            [
                new ProjectOptions
                {
                    Id = "proj-matemcp",
                    Name = "MateMCP",
                    Root = _root,
                    Read = true,
                    Write = true,
                    Shell = true
                }
            ]
        };
        return new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
