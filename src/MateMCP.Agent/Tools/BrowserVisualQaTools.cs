using System.ComponentModel;
using System.Text.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Browser;
using MateMCP.Agent.Desktop;
using MateMCP.Agent.Security;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class BrowserVisualQaTools(ApprovalService approvals, AuditLog audit)
{
    private readonly BrowserVisualQaService _visual = BrowserVisualQaService.Shared;
    private readonly ComputerUseSessionManager _computerUse = ComputerUseSessionManager.Shared;

    [McpServerTool(Name = "visual_viewports", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists the standard responsive viewport presets used by MateMCP visual QA.")]
    public IReadOnlyList<VisualViewportPreset> Viewports() => BrowserVisualQaService.Presets;

    [McpServerTool(Name = "visual_capture", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Captures a stabilized browser screenshot plus bounded DOM/geometry metadata and stores a short-lived in-memory capture id for before/after comparison. Use preset desktop/laptop/tablet/mobile or explicit width+height. Screenshot pixels are never written to disk by this feature.")]
    public async Task<IEnumerable<ContentBlock>> Capture(
        [Description("Viewport preset: desktop, laptop, tablet, mobile. Omit when using explicit width and height; defaults to desktop when neither is supplied.")] string? preset = null,
        int? width = null,
        int? height = null,
        double deviceScaleFactor = 1,
        [Description("Additional stabilization delay after fonts and two animation frames, 0..5000 ms.")] int settleMs = 250,
        [Description("Maximum DOM elements in structural metadata, 1..1500.")] int maxElements = 500,
        bool fullPage = false,
        [Description("Disable CSS animations/transitions and hide the caret during the capture to reduce visual noise.")] bool disableAnimations = true,
        [Description("Optional CSS selectors hidden without collapsing layout during this capture. Useful for timestamps, cursors, ads, or other dynamic regions. Maximum 32 selectors.")] IReadOnlyList<string>? maskCss = null,
        CancellationToken cancellationToken = default)
    {
        if (SensitiveUiGuard.Shared.Active) throw new McpException(SensitiveUiGuard.CaptureBlockedMessage);
        EnsureComputerUseAvailable();
        var viewport = BrowserVisualQaService.ResolveViewport(preset, width, height, deviceScaleFactor);
        var assessment = ComputerUseRiskClassifier.AssessSemantic("view");
        var target = $"visual-capture:low:{viewport.Width}x{viewport.Height}@{viewport.DeviceScaleFactor:0.##}";
        var decision = await approvals.RequestComputerUseAsync(
            "browser.view",
            target,
            $"Capture stabilized browser visual QA state at {viewport.Width}x{viewport.Height}, scale {viewport.DeviceScaleFactor:0.##}; mask selectors={maskCss?.Count ?? 0}.",
            assessment,
            cancellationToken);
        await EnsureApprovedAsync(decision, target, cancellationToken);

        VisualCaptureResult capture;
        try
        {
            capture = await _visual.CaptureAsync(new VisualCaptureOptions(
                preset, width, height, deviceScaleFactor, settleMs, maxElements,
                fullPage, disableAnimations, maskCss), cancellationToken);
        }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }

        _computerUse.Touch("browser-view", $"visual-capture:{capture.Metadata.Id}");
        await audit.WriteAsync(
            "browser.visual.capture",
            capture.Metadata.Url,
            $"ok:id={capture.Metadata.Id}:viewport={capture.Metadata.Viewport.Width}x{capture.Metadata.Viewport.Height}:image={capture.Metadata.ImageWidth}x{capture.Metadata.ImageHeight}:elements={capture.Metadata.ElementCount}:masks={capture.Metadata.MaskCount}",
            cancellationToken);

        var metadata = JsonSerializer.Serialize(capture.Metadata);
        return
        [
            new TextContentBlock { Text = metadata },
            ImageContentBlock.FromBytes(capture.ImageBytes, capture.MimeType)
        ];
    }

    [McpServerTool(Name = "visual_compare", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Deterministically compares two short-lived visual_capture ids after decoding PNG pixels. Reports changed pixels/percentage and connected changed regions. tolerance is per RGBA channel (0..255) and defaults to 8 to suppress small anti-aliasing noise. Size mismatches are reported explicitly instead of resizing images.")]
    public async Task<VisualCompareResult> Compare(
        string beforeId,
        string afterId,
        [Description("Maximum per-channel RGBA difference ignored as rendering noise, 0..255.")] int tolerance = 8,
        CancellationToken cancellationToken = default)
    {
        EnsureComputerUseAvailable();
        var assessment = ComputerUseRiskClassifier.AssessSemantic("view");
        var target = "visual-compare:low";
        var decision = await approvals.RequestComputerUseAsync(
            "browser.view",
            target,
            $"Compare two short-lived browser visual QA captures with tolerance {Math.Clamp(tolerance, 0, 255)}.",
            assessment,
            cancellationToken);
        await EnsureApprovedAsync(decision, target, cancellationToken);

        VisualCompareResult result;
        try { result = _visual.Compare(beforeId, afterId, tolerance); }
        catch (KeyNotFoundException ex) { throw new McpException(ex.Message); }

        _computerUse.Touch("browser-view", "visual-compare");
        await audit.WriteAsync(
            "browser.visual.compare",
            "in-memory-captures",
            $"ok:comparable={result.Comparable}:sizeMismatch={result.SizeMismatch}:changed={result.ChangedPixels}:percent={result.ChangedPercent:0.######}:regions={result.Regions.Count}:tolerance={result.Tolerance}",
            cancellationToken);
        return result;
    }

    private async Task EnsureApprovedAsync(ApprovalDecision decision, string target, CancellationToken cancellationToken)
    {
        if (decision == ApprovalDecision.Deny)
        {
            await audit.WriteAsync("browser.view", target, "denied:approval", cancellationToken);
            throw new McpException("Visual QA action denied by the local user.");
        }
        if (decision == ApprovalDecision.Timeout)
        {
            await audit.WriteAsync("browser.view", target, "denied:approval-timeout", CancellationToken.None);
            throw new McpException("Visual QA action approval timed out.");
        }
    }

    private void EnsureComputerUseAvailable()
    {
        try { _computerUse.EnsureAvailable(); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }
}
