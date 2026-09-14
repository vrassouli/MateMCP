using System.Security.Cryptography;
using System.Text;

namespace MateMCP.Agent.Relay;

internal sealed class RelayOperationRegistry
{
    private readonly ILogger<RelayOperationRegistry> _logger;
    private readonly int _maxEntries;
    private readonly TimeSpan _retention;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private readonly Dictionary<string, OperationEntry> _operations = new(StringComparer.Ordinal);

    public RelayOperationRegistry(ILogger<RelayOperationRegistry> logger)
        : this(logger, 512, TimeSpan.FromMinutes(5), () => DateTimeOffset.UtcNow) { }

    internal RelayOperationRegistry(ILogger<RelayOperationRegistry> logger, int maxEntries, TimeSpan retention, Func<DateTimeOffset> utcNow)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        _logger = logger;
        _maxEntries = maxEntries;
        _retention = retention;
        _utcNow = utcNow;
    }

    internal int Count { get { lock (_gate) return _operations.Count; } }

    public async Task<RelayResponse> ExecuteAsync(
        RelayRequest request,
        Func<CancellationToken, Task<RelayResponse>> execute,
        CancellationToken executionCancellation,
        CancellationToken waitCancellation)
    {
        var operationId = string.IsNullOrWhiteSpace(request.OperationId) ? request.Id : request.OperationId;
        var fingerprint = Fingerprint(request);
        OperationEntry entry;
        var created = false;

        lock (_gate)
        {
            PruneLocked(_utcNow());
            if (_operations.TryGetValue(operationId, out entry!))
            {
                if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new RelayOperationConflictException(operationId);
            }
            else
            {
                EnsureCapacityLocked();
                entry = new OperationEntry(operationId, fingerprint, _utcNow());
                _operations.Add(operationId, entry);
                created = true;
            }
        }

        if (created)
        {
            _logger.LogInformation("Accepted Relay operation: operation={OperationId}; relayRequestId={RelayRequestId}", operationId, request.Id);
            _ = RunAsync(entry, execute, executionCancellation);
        }
        else
        {
            _logger.LogInformation("Deduplicated Relay operation: operation={OperationId}; relayRequestId={RelayRequestId}", operationId, request.Id);
        }

        var response = await entry.Completion.Task.WaitAsync(waitCancellation);
        return response with { Id = request.Id };
    }

    private async Task RunAsync(OperationEntry entry, Func<CancellationToken, Task<RelayResponse>> execute, CancellationToken executionCancellation)
    {
        try
        {
            entry.Completion.TrySetResult(await execute(executionCancellation));
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
            entry.Completion.TrySetCanceled(executionCancellation);
        }
        catch (Exception ex)
        {
            entry.Completion.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                entry.CompletedAt = _utcNow();
                if (entry.Completion.Task.IsCanceled) _operations.Remove(entry.OperationId);
            }
        }
    }

    private void EnsureCapacityLocked()
    {
        while (_operations.Count >= _maxEntries)
        {
            var completed = _operations.Values.Where(x => x.CompletedAt is not null).OrderBy(x => x.CompletedAt).FirstOrDefault();
            if (completed is null) throw new RelayOperationCapacityException(_maxEntries);
            _operations.Remove(completed.OperationId);
        }
    }

    private void PruneLocked(DateTimeOffset now)
    {
        foreach (var id in _operations.Values
                     .Where(x => x.CompletedAt is not null && now - x.CompletedAt.Value >= _retention)
                     .Select(x => x.OperationId)
                     .ToArray())
            _operations.Remove(id);
    }

    private static string Fingerprint(RelayRequest request)
    {
        var value = $"{request.Method}\n{request.Path}\n{request.BodyBase64 ?? string.Empty}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private sealed class OperationEntry(string operationId, string fingerprint, DateTimeOffset createdAt)
    {
        public string OperationId { get; } = operationId;
        public string Fingerprint { get; } = fingerprint;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset? CompletedAt { get; set; }
        public TaskCompletionSource<RelayResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class RelayOperationConflictException(string operationId)
    : InvalidOperationException($"OperationId '{operationId}' was reused for a different request payload.");

internal sealed class RelayOperationCapacityException(int maxEntries)
    : InvalidOperationException($"The Relay operation registry reached its bounded capacity of {maxEntries} active entries.");
