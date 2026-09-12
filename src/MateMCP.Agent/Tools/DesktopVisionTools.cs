using System.ComponentModel;
using System.Text.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Desktop;
using MateMCP.Agent.Security;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class DesktopVisionTools(AuditLog audit, ApprovalService approvals)
{
    private readonly DesktopVisionService _vision = new();

    [McpServerTool(Name = "screen_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists displays available to the local MateMCP Agent, including logical bounds, scale factor, and primary-display status. Desktop visual inspection is privacy-sensitive and requires local approval unless an applicable session/persistent policy already exists.")]
    public async Task<IReadOnlyList<DesktopScreenInfo>> ListScreens(CancellationToken cancellationToken = default)
    {
        await RequireViewApprovalAsync("Inspect the local display layout (screen identifiers, bounds, scale, and primary-display state).", cancellationToken);
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

    [McpServerTool(Name = "window_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists visible top-level application windows available to the local MateMCP Agent. Window titles/application identities can be privacy-sensitive, so visual inspection requires local approval unless an applicable session/persistent policy already exists. Use the returned window id with screen_capture target=window.")]
    public async Task<IReadOnlyList<DesktopWindowInfo>> ListWindows(CancellationToken cancellationToken = default)
    {
        await RequireViewApprovalAsync("Inspect visible local application windows, including titles, application identity, and geometry.", cancellationToken);
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

    [McpServerTool(Name = "screen_capture", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Captures a display, visible application window, or rectangular desktop region and returns both capture metadata and an inspectable PNG image. Desktop pixels can contain private information, so visual inspection requires local approval unless an applicable session/persistent policy already exists. target must be screen, window, or region. For screen/window use ids from screen_list/window_list; omitted screen id means the primary display.")]
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
        var targetSummary = normalized switch
        {
            "window" => $"Capture pixels from local window id {id ?? "<missing>"}.",
            "region" => $"Capture local desktop pixels from region ({x},{y}) {width}x{height}.",
            _ => $"Capture pixels from local display {id ?? "primary"}."
        };
        await RequireViewApprovalAsync(targetSummary, cancellationToken);

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

    private async Task RequireViewApprovalAsync(string summary, CancellationToken cancellationToken)
    {
        var decision = await approvals.RequestAsync("desktop.view", "visual-inspection", summary, cancellationToken);
        if (decision == ApprovalDecision.Deny)
        {
            await audit.WriteAsync("desktop.view", "visual-inspection", "denied:approval", cancellationToken);
            throw new McpException("Desktop visual inspection denied by the local user.");
        }
        if (decision == ApprovalDecision.Timeout)
        {
            await audit.WriteAsync("desktop.view", "visual-inspection", "denied:approval-timeout", CancellationToken.None);
            throw new McpException("Desktop visual inspection approval timed out.");
        }
    }
}
