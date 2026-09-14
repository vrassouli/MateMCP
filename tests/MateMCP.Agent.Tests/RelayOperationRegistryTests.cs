using MateMCP.Agent.Relay;
using Microsoft.Extensions.Logging.Abstractions;

namespace MateMCP.Agent.Tests;

public sealed class RelayOperationRegistryTests
{
    [Fact]
    public async Task Concurrent_duplicate_operation_executes_once_and_rebinds_response_id()
    {
        var registry = NewRegistry();
        var started = NewTcs();
        var release = NewTcs();
        var executions = 0;

        async Task<RelayResponse> Execute(CancellationToken ct)
        {
            Interlocked.Increment(ref executions);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response("first-transport");
        }

        var first = registry.ExecuteAsync(Request("request-1", "operation-1"), Execute, CancellationToken.None, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = registry.ExecuteAsync(Request("request-2", "operation-1"), Execute, CancellationToken.None, CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref executions));
        release.TrySetResult();

        var firstResponse = await first;
        var secondResponse = await second;
        Assert.Equal("request-1", firstResponse.Id);
        Assert.Equal("request-2", secondResponse.Id);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Completed_duplicate_replays_cached_result_without_reexecution()
    {
        var registry = NewRegistry();
        var executions = 0;
        Task<RelayResponse> Execute(CancellationToken _)
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult(Response("original"));
        }

        var first = await registry.ExecuteAsync(Request("r1", "op"), Execute, CancellationToken.None, CancellationToken.None);
        var replay = await registry.ExecuteAsync(Request("r2", "op"), Execute, CancellationToken.None, CancellationToken.None);

        Assert.Equal(1, executions);
        Assert.Equal("r1", first.Id);
        Assert.Equal("r2", replay.Id);
        Assert.Equal(first.StatusCode, replay.StatusCode);
    }

    [Fact]
    public async Task Transport_wait_cancellation_does_not_cancel_running_operation()
    {
        var registry = NewRegistry();
        var started = NewTcs();
        var release = NewTcs();
        var executions = 0;
        using var firstTransport = new CancellationTokenSource();

        async Task<RelayResponse> Execute(CancellationToken ct)
        {
            Interlocked.Increment(ref executions);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response("old-request");
        }

        var first = registry.ExecuteAsync(Request("old-request", "stable-op"), Execute, CancellationToken.None, firstTransport.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        firstTransport.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var recovered = registry.ExecuteAsync(Request("new-request", "stable-op"), Execute, CancellationToken.None, CancellationToken.None);
        release.TrySetResult();
        var response = await recovered;

        Assert.Equal(1, executions);
        Assert.Equal("new-request", response.Id);
    }

    [Fact]
    public async Task Conflicting_operation_id_reuse_is_rejected()
    {
        var registry = NewRegistry();
        await registry.ExecuteAsync(Request("r1", "same-op", "payload-a"), _ => Task.FromResult(Response("r1")), CancellationToken.None, CancellationToken.None);

        await Assert.ThrowsAsync<RelayOperationConflictException>(() =>
            registry.ExecuteAsync(Request("r2", "same-op", "payload-b"), _ => Task.FromResult(Response("r2")), CancellationToken.None, CancellationToken.None));
    }

    [Fact]
    public async Task Completed_entries_are_bounded_and_expire()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new RelayOperationRegistry(NullLogger<RelayOperationRegistry>.Instance, 2, TimeSpan.FromMinutes(1), () => now);

        await registry.ExecuteAsync(Request("r1", "op1"), _ => Task.FromResult(Response("r1")), CancellationToken.None, CancellationToken.None);
        await registry.ExecuteAsync(Request("r2", "op2"), _ => Task.FromResult(Response("r2")), CancellationToken.None, CancellationToken.None);
        Assert.Equal(2, registry.Count);

        await registry.ExecuteAsync(Request("r3", "op3"), _ => Task.FromResult(Response("r3")), CancellationToken.None, CancellationToken.None);
        Assert.Equal(2, registry.Count);

        now = now.AddMinutes(2);
        await registry.ExecuteAsync(Request("r4", "op4"), _ => Task.FromResult(Response("r4")), CancellationToken.None, CancellationToken.None);
        Assert.Equal(1, registry.Count);
    }

    private static RelayOperationRegistry NewRegistry()
        => new(NullLogger<RelayOperationRegistry>.Instance, 32, TimeSpan.FromMinutes(5), () => DateTimeOffset.UtcNow);

    private static RelayRequest Request(string id, string operationId, string payload = "payload")
        => new(id, "POST", "/mcp", new(), Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)), operationId);

    private static RelayResponse Response(string id) => new(id, 200, new(), "cmVzdWx0", null);
    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
