using MateMCP.Agent.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MateMCP.Agent.Tests;

public sealed class AgentLogProviderNoiseTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "matemcp-agent-log-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RoutineAspNetCoreInformation_IsNotStoredByDefault()
    {
        var store = CreateStore();
        using var provider = new AgentLogProvider(store, verboseHttpLogging: false);
        var logger = provider.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");

        logger.LogInformation("Request starting HTTP/1.1 GET http://127.0.0.1/logs");

        Assert.Empty(store.Read());
    }

    [Fact]
    public void AspNetCoreFailedResponse_IsStillStored()
    {
        var store = CreateStore();
        using var provider = new AgentLogProvider(store, verboseHttpLogging: false);
        var logger = provider.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
        var state = new List<KeyValuePair<string, object?>>
        {
            new("StatusCode", 500),
            new("Path", "/status")
        };

        logger.Log(LogLevel.Information, new EventId(2, "RequestFinished"), state, null,
            static (_, _) => "Request finished HTTP/1.1 GET /status - 500");

        var entry = Assert.Single(store.Read());
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("500", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AspNetCoreWarnings_AreStillStored()
    {
        var store = CreateStore();
        using var provider = new AgentLogProvider(store, verboseHttpLogging: false);
        var logger = provider.CreateLogger("Microsoft.AspNetCore.Server.Kestrel");

        logger.LogWarning("Unexpected connection failure");

        var entry = Assert.Single(store.Read());
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Fact]
    public void ApplicationInformation_IsStillStored()
    {
        var store = CreateStore();
        using var provider = new AgentLogProvider(store, verboseHttpLogging: false);
        var logger = provider.CreateLogger("MateMCP.Agent.Relay.RelayConnector");

        logger.LogInformation("Relay reconnected successfully");

        var entry = Assert.Single(store.Read());
        Assert.Contains("Relay reconnected", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerboseHttpMode_RestoresAspNetCoreInformation()
    {
        var store = CreateStore();
        using var provider = new AgentLogProvider(store, verboseHttpLogging: true);
        var logger = provider.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");

        logger.LogInformation("Request starting HTTP/1.1 GET http://127.0.0.1/logs");

        var entry = Assert.Single(store.Read());
        Assert.Contains("Request starting", entry.Message, StringComparison.Ordinal);
    }

    private AgentLogStore CreateStore()
    {
        Directory.CreateDirectory(_tempDir);
        return new AgentLogStore(Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".jsonl"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
