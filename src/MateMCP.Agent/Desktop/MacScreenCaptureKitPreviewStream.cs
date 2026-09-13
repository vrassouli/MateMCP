using System.Buffers.Binary;
using System.Diagnostics;

namespace MateMCP.Agent.Desktop;

/// <summary>
/// Keeps one persistent ScreenCaptureKit helper for the window currently shown in
/// Companion's Computer Use preview. The helper is native Swift and emits framed
/// JPEGs over stdout. It is intentionally local-only and never participates in audit.
/// </summary>
internal sealed class MacScreenCaptureKitPreviewStream : IDisposable
{
    private const string HelperName = "MateMCP.ScreenCaptureKitHelper";
    private static readonly TimeSpan FreshFrameAge = TimeSpan.FromSeconds(3);

    private readonly object _sync = new();
    private Process? _process;
    private CancellationTokenSource? _readerCts;
    private string? _windowId;
    private byte[]? _latestFrame;
    private int _latestWidth;
    private int _latestHeight;
    private DateTimeOffset? _latestAt;
    private string? _lastError;
    private bool _disposed;

    public bool Available
        => OperatingSystem.IsMacOS() && File.Exists(ResolveHelperPath());

    public string BackendName
        => Available ? "macos-screencapturekit-stream" : "os-screenshot-fallback";

    public string? LastError
    {
        get { lock (_sync) return _lastError; }
    }

    public void EnsureStarted(string windowId)
    {
        if (!Available || string.IsNullOrWhiteSpace(windowId)) return;
        lock (_sync)
        {
            if (_disposed) return;
            if (string.Equals(_windowId, windowId, StringComparison.Ordinal)
                && _process is { HasExited: false })
                return;

            StopLocked();
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = ResolveHelperPath(),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add(windowId);
                start.ArgumentList.Add("4");
                var process = Process.Start(start)
                    ?? throw new InvalidOperationException("Could not start the ScreenCaptureKit preview helper.");
                var cts = new CancellationTokenSource();

                _process = process;
                _readerCts = cts;
                _windowId = windowId;
                _latestFrame = null;
                _latestWidth = 0;
                _latestHeight = 0;
                _latestAt = null;
                _lastError = null;

                _ = Task.Run(() => ReadFramesAsync(process, windowId, cts.Token));
                _ = Task.Run(() => ReadErrorsAsync(process, windowId, cts.Token));
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                StopLocked();
            }
        }
    }

    public async Task<DesktopCaptureResult?> WaitForFrameAsync(
        string windowId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EnsureStarted(windowId);
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = TryGetFrame(windowId);
            if (frame is not null) return frame;
            await Task.Delay(25, cancellationToken);
        }
        return TryGetFrame(windowId);
    }

    public DesktopCaptureResult? TryGetFrame(string windowId)
    {
        lock (_sync)
        {
            if (!string.Equals(_windowId, windowId, StringComparison.Ordinal)
                || _latestFrame is null
                || _latestWidth <= 0
                || _latestHeight <= 0
                || _latestAt is null
                || DateTimeOffset.UtcNow - _latestAt > FreshFrameAge)
                return null;

            return new DesktopCaptureResult(
                _latestFrame,
                "image/jpeg",
                _latestWidth,
                _latestHeight,
                "window",
                windowId);
        }
    }

    public void Stop()
    {
        lock (_sync) StopLocked();
    }

    private async Task ReadFramesAsync(Process process, string windowId, CancellationToken cancellationToken)
    {
        var stream = process.StandardOutput.BaseStream;
        var header = new byte[12];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, cancellationToken);
                var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)));
                var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)));
                var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4)));
                if (length <= 0 || length > DesktopVisionService.MaxCaptureBytes || width <= 0 || height <= 0)
                    throw new InvalidDataException("ScreenCaptureKit helper emitted an invalid frame header.");

                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, cancellationToken);
                lock (_sync)
                {
                    if (_process != process || !string.Equals(_windowId, windowId, StringComparison.Ordinal))
                        return;
                    _latestFrame = bytes;
                    _latestWidth = width;
                    _latestHeight = height;
                    _latestAt = DateTimeOffset.UtcNow;
                    _lastError = null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (_process == process) _lastError = ex.Message;
            }
        }
    }

    private async Task ReadErrorsAsync(Process process, string windowId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                lock (_sync)
                {
                    if (_process == process && string.Equals(_windowId, windowId, StringComparison.Ordinal))
                        _lastError = line.Trim();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { }
    }

    private void StopLocked()
    {
        var process = _process;
        var cts = _readerCts;
        _process = null;
        _readerCts = null;
        _windowId = null;
        _latestFrame = null;
        _latestWidth = 0;
        _latestHeight = 0;
        _latestAt = null;

        try { cts?.Cancel(); } catch { }
        try { process?.StandardInput.Close(); } catch { }
        try
        {
            if (process is { HasExited: false } && !process.WaitForExit(250))
                process.Kill(entireProcessTree: true);
        }
        catch { }
        try { process?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private static string ResolveHelperPath()
    {
        var configured = Environment.GetEnvironmentVariable("MATEMCP_SCREEN_CAPTURE_KIT_HELPER");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, HelperName)
            : configured;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            StopLocked();
        }
    }
}
