using System.Collections.Concurrent;

namespace MateMCP.Agent.Relay;

internal sealed class RelayRequestScheduler : IAsyncDisposable
{
    private readonly Func<RelayRequest, CancellationToken, Task<RelayResponse>> _forward;
    private readonly Func<RelayResponse, CancellationToken, Task> _send;
    private readonly Action<Exception>? _onTransportFailure;
    private readonly SemaphoreSlim _concurrency;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

    public RelayRequestScheduler(
        int maxConcurrency,
        Func<RelayRequest, CancellationToken, Task<RelayResponse>> forward,
        Func<RelayResponse, CancellationToken, Task> send,
        CancellationToken connectionToken,
        Action<Exception>? onTransportFailure = null)
    {
        if (maxConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _forward = forward;
        _send = send;
        _onTransportFailure = onTransportFailure;
        _concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
    }

    public int InFlightCount
    {
        get { lock (_gate) return _inFlight.Count; }
    }

    public bool TryQueue(RelayRequest request)
    {
        Task task;
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested || _inFlight.ContainsKey(request.Id)) return false;
            task = ProcessAsync(request);
            _inFlight.Add(request.Id, task);
        }

        _ = ObserveCompletionAsync(request.Id, task);
        return true;
    }

    public async Task DrainAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (_gate) snapshot = _inFlight.Values.ToArray();
            if (snapshot.Length == 0) return;
            try { await Task.WhenAll(snapshot); }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await DrainAsync();
        _lifetime.Dispose();
        _concurrency.Dispose();
        _sendLock.Dispose();
    }

    private async Task ProcessAsync(RelayRequest request)
    {
        var gateHeld = false;
        try
        {
            await _concurrency.WaitAsync(_lifetime.Token);
            gateHeld = true;

            RelayResponse response;
            try
            {
                response = await _forward(request, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                response = new RelayResponse(request.Id, 502, new(), null, ex.Message);
            }

            await _sendLock.WaitAsync(_lifetime.Token);
            try
            {
                await _send(response, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _onTransportFailure?.Invoke(ex);
                _lifetime.Cancel();
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            if (gateHeld) _concurrency.Release();
        }
    }

    private async Task ObserveCompletionAsync(string requestId, Task task)
    {
        try { await task; }
        finally
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(requestId, out var current) && ReferenceEquals(current, task))
                    _inFlight.Remove(requestId);
            }
        }
    }
}

internal sealed record RelayRequest(string Id, string Method, string Path, Dictionary<string, string[]> Headers, string? BodyBase64);
internal sealed record RelayResponse(string Id, int StatusCode, Dictionary<string, string[]> Headers, string? BodyBase64, string? Error);
