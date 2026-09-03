namespace MateMCP.Agent.Tests;

public sealed class AgentLogsCompanionTests
{
    [Fact]
    public void Companion_exposes_live_filterable_copyable_agent_logs()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        var panel = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "AgentLogsPanel.razor"));
        var index = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "index.html"));
        var client = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Services", "AgentApiClient.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Program.cs"));

        Assert.Contains("Agent Logs", main, StringComparison.Ordinal);
        Assert.Contains("<AgentLogsPanel />", main, StringComparison.Ordinal);
        Assert.Contains("Live updates", panel, StringComparison.Ordinal);
        Assert.Contains("Minimum level", panel, StringComparison.Ordinal);
        Assert.Contains("Message or category", panel, StringComparison.Ordinal);
        Assert.Contains("Copy diagnostics", panel, StringComparison.Ordinal);
        Assert.Contains("Confirm clear", panel, StringComparison.Ordinal);
        Assert.Contains("class=\"terminal log-terminal\"", panel, StringComparison.Ordinal);
        Assert.Contains("PeriodicTimer(TimeSpan.FromSeconds(1))", panel, StringComparison.Ordinal);
        Assert.Contains("follow = nearBottom()", index, StringComparison.Ordinal);
        Assert.Contains("if (!follow) return", index, StringComparison.Ordinal);
        Assert.Contains("GetAgentLogsAsync", client, StringComparison.Ordinal);
        Assert.Contains("ClearAgentLogsAsync", client, StringComparison.Ordinal);
        Assert.Contains("app.MapGet(\"/logs\"", program, StringComparison.Ordinal);
        Assert.Contains("app.MapDelete(\"/logs\"", program, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MateMCP.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
