namespace MateMCP.Agent.Desktop;

public sealed record ComputerUsePreviewState(
    bool Active,
    bool Blocked,
    string? WindowId,
    string? WindowTitle,
    string? Application,
    int? ProcessId,
    double? CursorX,
    double? CursorY,
    string? LastAction,
    string CaptureBackend,
    string? CaptureError,
    DateTimeOffset? UpdatedAt,
    long Revision);

/// <summary>
/// Maintains the local-only view of the window currently being used by Computer Use.
/// Cursor coordinates are normalized to the target window (0..1), so Companion can
/// render an AI-only cursor without moving the physical OS pointer.
/// </summary>
public sealed class ComputerUsePreviewService : IDisposable
{
    private readonly object _sync = new();
    private readonly DesktopVisionService _vision = new();
    private readonly ComputerUseSessionManager _computerUse = ComputerUseSessionManager.Shared;
    private readonly MacScreenCaptureKitPreviewStream _macStream = new();

    private DesktopWindowInfo? _window;
    private double? _cursorX;
    private double? _cursorY;
    private string? _lastAction;
    private DateTimeOffset? _updatedAt;
    private long _revision;

    public async Task TrackWindowAsync(
        string windowId,
        UiRect? actionBounds = null,
        string? action = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;

        var windows = await _vision.ListWindowsAsync(cancellationToken);
        var window = windows.FirstOrDefault(item => string.Equals(item.Id, windowId, StringComparison.OrdinalIgnoreCase));
        if (window is null) return;

        lock (_sync)
        {
            _window = window;
            if (actionBounds is not null && window.Width > 0 && window.Height > 0)
            {
                var cursor = NormalizeCursor(window, actionBounds);
                _cursorX = cursor.X;
                _cursorY = cursor.Y;
            }

            _lastAction = string.IsNullOrWhiteSpace(action) ? _lastAction : action.Trim();
            _updatedAt = DateTimeOffset.UtcNow;
            _revision++;
        }

        if (OperatingSystem.IsMacOS()) _macStream.EnsureStarted(window.Id);
    }

    public async Task TrackWindowPointAsync(
        string windowId,
        double x,
        double y,
        string? action = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        var windows = await _vision.ListWindowsAsync(cancellationToken);
        var window = windows.FirstOrDefault(item => string.Equals(item.Id, windowId, StringComparison.OrdinalIgnoreCase));
        if (window is null || window.Width <= 0 || window.Height <= 0) return;

        lock (_sync)
        {
            _window = window;
            _cursorX = Math.Clamp(x / window.Width, 0d, 1d);
            _cursorY = Math.Clamp(y / window.Height, 0d, 1d);
            _lastAction = string.IsNullOrWhiteSpace(action) ? _lastAction : action.Trim();
            _updatedAt = DateTimeOffset.UtcNow;
            _revision++;
        }

        if (OperatingSystem.IsMacOS()) _macStream.EnsureStarted(window.Id);
    }

    internal static (double X, double Y) NormalizeCursor(DesktopWindowInfo window, UiRect bounds)
    {
        if (window.Width <= 0 || window.Height <= 0) return (0.5d, 0.5d);
        var centerX = bounds.X + bounds.Width / 2d;
        var centerY = bounds.Y + bounds.Height / 2d;
        return (
            Math.Clamp((centerX - window.X) / window.Width, 0d, 1d),
            Math.Clamp((centerY - window.Y) / window.Height, 0d, 1d));
    }

    public ComputerUsePreviewState GetState()
    {
        var use = _computerUse.GetStatus();
        lock (_sync)
        {
            var active = use.Active && !use.Blocked && _window is not null;
            if (!active && OperatingSystem.IsMacOS()) _macStream.Stop();
            return new ComputerUsePreviewState(
                active,
                use.Blocked,
                active ? _window!.Id : null,
                active ? _window!.Title : null,
                active ? _window!.Application : null,
                active ? _window!.ProcessId : null,
                active ? _cursorX : null,
                active ? _cursorY : null,
                active ? _lastAction : null,
                OperatingSystem.IsMacOS() ? _macStream.BackendName : "os-screenshot-fallback",
                OperatingSystem.IsMacOS() ? _macStream.LastError : null,
                _updatedAt,
                _revision);
        }
    }

    public async Task<DesktopCaptureResult?> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        var state = GetState();
        if (!state.Active || string.IsNullOrWhiteSpace(state.WindowId)) return null;

        if (OperatingSystem.IsMacOS() && _macStream.Available)
        {
            var native = await _macStream.WaitForFrameAsync(
                state.WindowId,
                TimeSpan.FromMilliseconds(350),
                cancellationToken);
            if (native is not null) return native;
        }

        try
        {
            return await _vision.CaptureAsync("window", state.WindowId, cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose() => _macStream.Dispose();
}
