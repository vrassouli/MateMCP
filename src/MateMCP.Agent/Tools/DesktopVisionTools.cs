using System.ComponentModel;
using System.Text.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Desktop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class DesktopVisionTools(AuditLog audit)
{
    private readonly DesktopVisionService _vision = new();

    [McpServerTool(Name = "screen_list"), Description("Lists displays available to the local MateMCP Agent, including logical bounds, scale factor, and primary-display status.")]
    public async Task<IReadOnlyList<DesktopScreenInfo>> ListScreens(CancellationToken cancellationToken = default)
    {
        try
        {
            var screens = await _vision.ListScreensAsync(cancellationToken);
            await audit.WriteAsync("desktop.screen.list", "local-desktop", $"ok:{screens.Count}", cancellationToken);
            return screens;
        }
        catch (Exception ex)
        {
            await audit.WriteAsync("desktop.screen.list", "local-desktop", $"error:{ex.GetType().Name}", cancellationToken);
            throw;
        }
    }

    [McpServerTool(Name = "window_list"), Description("Lists visible top-level application windows available to the local MateMCP Agent. Use the returned window id with screen_capture target=window.")]
    public async Task<IReadOnlyList<DesktopWindowInfo>> ListWindows(CancellationToken cancellationToken = default)
    {
        try
        {
            var windows = await _vision.ListWindowsAsync(cancellationToken);
            await audit.WriteAsync("desktop.window.list", "local-desktop", $"ok:{windows.Count}", cancellationToken);
            return windows;
        }
        catch (Exception ex)
        {
            await audit.WriteAsync("desktop.window.list", "local-desktop", $"error:{ex.GetType().Name}", cancellationToken);
            throw;
        }
    }

    [McpServerTool(Name = "screen_capture"), Description("Captures a display, visible application window, or rectangular desktop region and returns both capture metadata and an inspectable PNG image. target must be screen, window, or region. For screen/window use ids from screen_list/window_list; omitted screen id means the primary display.")]
    public async Task<IEnumerable<ContentBlock>> Capture(
        [Description("Capture target: screen, window, or region.")] string target = "screen",
        [Description("Display/window id returned by screen_list or window_list. Optional for target=screen; required for target=window.")] string? id = null,
        [Description("Region X coordinate in global desktop logical coordinates; required only for target=region.")] int? x = null,
        [Description("Region Y coordinate in global desktop logical coordinates; required only for target=region.")] int? y = null,
        [Description("Region width in global desktop logical coordinates; required only for target=region.")] int? width = null,
        [Description("Region height in global desktop logical coordinates; required only for target=region.")] int? height = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = DesktopVisionService.NormalizeTarget(target);
        try
        {
            var capture = await _vision.CaptureAsync(normalized, id, x, y, width, height, cancellationToken);
            await audit.WriteAsync("desktop.screen.capture", $"{capture.Target}:{capture.TargetId ?? "default"}", $"ok:{capture.Width}x{capture.Height}:{capture.Bytes.Length}", cancellationToken);

            var metadata = JsonSerializer.Serialize(new
            {
                capture.Target,
                capture.TargetId,
                capture.Width,
                capture.Height,
                capture.MimeType,
                bytes = capture.Bytes.Length,
                coordinateSpace = "global desktop logical coordinates; image pixels may be scaled on HiDPI displays"
            });

            return
            [
                new TextContentBlock { Text = metadata },
                ImageContentBlock.FromBytes(capture.Bytes, capture.MimeType)
            ];
        }
        catch (Exception ex)
        {
            await audit.WriteAsync("desktop.screen.capture", $"{normalized}:{id ?? "default"}", $"error:{ex.GetType().Name}", cancellationToken);
            throw;
        }
    }
}
