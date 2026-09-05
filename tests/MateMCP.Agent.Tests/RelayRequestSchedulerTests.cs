using System.Collections.Concurrent;
using MateMCP.Agent.Relay;

namespace MateMCP.Agent.Tests;

public sealed class RelayRequestSchedulerTests
{
    [Fact]
    public async Task Two_short_requests_are_processed_and_sent()
    {
        var sent = new ConcurrentBag<string>();
        await using var scheduler = CreateScheduler(2,
            (request, _) => Task.FromResult(Response(request.Id)),
            (response, _) => { sent.Add(response.Id); return Task.CompletedTask; });

        Assert.True(scheduler.TryQueue(Request("one")));
        Assert.True(scheduler.TryQueue(Request("two")));
        await scheduler.DrainAsync();

        Assert.Equal(2, sent.Count);
        Assert.Contains("one", sent);
        Assert.Contains("two", sent);
    }

    [Fact]
    public async Task Short_request_is_not_blocked_by_long_request()
    {
        var longStarted = NewTcs();
        var releaseLong = NewTcs();
        var shortSent = NewTcs();

        await using var scheduler = CreateScheduler(2,
            async (request, ct) =>
            {
                if (request.Id == "long")
                {
                    longStarted.TrySetResult();
                    await releaseLong.Task.WaitAsync(ct);
                }
                return Response(request.Id);
            },
            (response, _) =>
            {
                if (response.Id == "short") shortSent.TrySetResult();
                return Task.CompletedTask;
            });

        Assert.True(scheduler.TryQueue(Request("long")));
        await longStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(scheduler.TryQueue(Request("short")));

        await shortSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(releaseLong.Task.IsCompleted);
        releaseLong.TrySetResult();
        await scheduler.DrainAsync();
    }

    [Fact]
    public async Task Two_long_requests_can_execute_concurrently()
    {
        var bothStarted = NewTcs();
        var release = NewTcs();
        var active = 0;
        var maxActive = 0;

        await using var scheduler = CreateScheduler(2,
            async (request, ct) =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maxActive, now);
                if (now == 2) bothStarted.TrySetResult();
                await release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref active);
                return Response(request.Id);
            },
            (_, _) => Task.CompletedTask);

        Assert.True(scheduler.TryQueue(Request("long-1")));
        Assert.True(scheduler.TryQueue(Request("long-2")));
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, maxActive);
        release.TrySetResult();
        await scheduler.DrainAsync();
    }

    [Fact]
    public async Task Shell_and_filesystem_like_requests_overlap()
    {
        var started = new ConcurrentDictionary<string, bool>();
        var bothStarted = NewTcs();
        var release = NewTcs();

        await using var scheduler = CreateScheduler(2,
            async (request, ct) =>
            {
                started[request.Path] = true;
                if (started.Count == 2) bothStarted.TrySetResult();
                await release.Task.WaitAsync(ct);
                return Response(request.Id);
            },
            (_, _) => Task.CompletedTask);

        Assert.True(scheduler.TryQueue(new RelayRequest("shell", "POST", "/mcp?shell", new(), null)));
        Assert.True(scheduler.TryQueue(new RelayRequest("fs", "POST", "/mcp?filesystem", new(), null)));
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains("/mcp?shell", started.Keys);
        Assert.Contains("/mcp?filesystem", started.Keys);
        release.TrySetResult();
        await scheduler.DrainAsync();
    }

    [Fact]
    public async Task Repeated_bursts_are_bounded_by_configured_concurrency()
    {
        var active = 0;
        var maxActive = 0;
        var sent = 0;

        await using var scheduler = CreateScheduler(4,
            async (request, ct) =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maxActive, now);
                await Task.Delay(10, ct);
                Interlocked.Decrement(ref active);
                return Response(request.Id);
            },
            (_, _) => { Interlocked.Increment(ref sent); return Task.CompletedTask; });

        for (var burst = 0; burst < 5; burst++)
            for (var i = 0; i < 20; i++)
                Assert.True(scheduler.TryQueue(Request($"b{burst}-{i}")));

        await scheduler.DrainAsync();

        Assert.Equal(100, sent);
        Assert.InRange(maxActive, 2, 4);
    }

    [Fact]
    public async Task Duplicate_request_id_is_not_executed_twice()
    {
        var release = NewTcs();
        var executions = 0;

        await using var scheduler = CreateScheduler(1,
            async (request, ct) =>
            {
                Interlocked.Increment(ref executions);
                await release.Task.WaitAsync(ct);
                return Response(request.Id);
            },
            (_, _) => Task.CompletedTask);

        Assert.True(scheduler.TryQueue(Request("same")));
        Assert.False(scheduler.TryQueue(Request("same")));
        release.TrySetResult();
        await scheduler.DrainAsync();

        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Local_request_failure_returns_error_without_failing_transport()
    {
        var transportFailures = 0;
        RelayResponse? sent = null;
        await using var scheduler = new RelayRequestScheduler(
            2,
            (_, _) => throw new InvalidOperationException("local failure"),
            (response, _) => { sent = response; return Task.CompletedTask; },
            CancellationToken.None,
            _ => Interlocked.Increment(ref transportFailures));

        Assert.True(scheduler.TryQueue(Request("failed")));
        await scheduler.DrainAsync();

        Assert.NotNull(sent);
        Assert.Equal(502, sent.StatusCode);
        Assert.Equal("failed", sent.Id);
        Assert.Equal(0, transportFailures);
    }

    [Fact]
    public async Task Response_writes_are_serialized()
    {
        var sendActive = 0;
        var maxSendActive = 0;

        await using var scheduler = CreateScheduler(8,
            (request, _) => Task.FromResult(Response(request.Id)),
            async (_, ct) =>
            {
                var now = Interlocked.Increment(ref sendActive);
                InterlockedExtensions.Max(ref maxSendActive, now);
                await Task.Delay(5, ct);
                Interlocked.Decrement(ref sendActive);
            });

        for (var i = 0; i < 25; i++) Assert.True(scheduler.TryQueue(Request(i.ToString())));
        await scheduler.DrainAsync();

        Assert.Equal(1, maxSendActive);
    }

    [Fact]
    public async Task Connection_shutdown_cancels_and_drains_workers()
    {
        using var connectionLifetime = new CancellationTokenSource();
        var started = NewTcs();
        var cancellationObserved = NewTcs();
        await using var scheduler = new RelayRequestScheduler(
            2,
            async (request, ct) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return Response(request.Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            },
            (_, _) => Task.CompletedTask,
            connectionLifetime.Token);

        Assert.True(scheduler.TryQueue(Request("shutdown")));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        connectionLifetime.Cancel();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await scheduler.DrainAsync();

        Assert.Equal(0, scheduler.InFlightCount);
    }

    [Fact]
    public async Task Response_transport_failure_is_fatal_to_connection_lifetime()
    {
        var failureObserved = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new RelayRequestScheduler(
            2,
            (request, _) => Task.FromResult(Response(request.Id)),
            (_, _) => throw new IOException("socket send failed"),
            CancellationToken.None,
            ex => failureObserved.TrySetResult(ex));

        Assert.True(scheduler.TryQueue(Request("transport-failure")));
        var failure = await failureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await scheduler.DrainAsync();

        Assert.IsType<IOException>(failure);
        Assert.Equal(0, scheduler.InFlightCount);
        Assert.False(scheduler.TryQueue(Request("after-failure")));
    }
    private static RelayRequestScheduler CreateScheduler(
        int concurrency,
        Func<RelayRequest, CancellationToken, Task<RelayResponse>> forward,
        Func<RelayResponse, CancellationToken, Task> send)
        => new(concurrency, forward, send, CancellationToken.None);

    private static RelayRequest Request(string id) => new(id, "POST", "/mcp", new(), null);
    private static RelayResponse Response(string id) => new(id, 200, new(), null, null);
    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (current >= value) return;
                if (Interlocked.CompareExchange(ref target, value, current) == current) return;
            }
        }
    }
}
