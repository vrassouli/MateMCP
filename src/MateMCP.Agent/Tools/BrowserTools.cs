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
public sealed class BrowserTools(ApprovalService approvals, AuditLog audit)
{
    private readonly BrowserAutomationService _browser = BrowserAutomationService.Shared;
    private readonly ComputerUseSessionManager _computerUse = ComputerUseSessionManager.Shared;

    [McpServerTool(Name = "browser_open", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Opens or navigates MateMCP's dedicated temporary-profile Chrome/Edge browser session to an http(s) URL. The dedicated profile does not silently inherit the user's normal browser cookies/session.")]
    public async Task<BrowserSessionStatus> Open(
        [Description("Absolute http:// or https:// URL, including localhost development URLs.")] string url,
        [Description("Browser channel: auto, chrome, or msedge.")] string channel = "auto",
        CancellationToken cancellationToken = default)
    {
        var uri = BrowserAutomationService.ValidateUrl(url);
        await RequireApprovalAsync("browser.navigate", "navigation", $"Open or navigate the dedicated browser to {uri.AbsoluteUri}.", cancellationToken);
        EnsureComputerUseAvailable();
        var status = await _browser.OpenAsync(uri.AbsoluteUri, channel, cancellationToken);
        _computerUse.Touch("browser-navigation", status.Url);
        await audit.WriteAsync("browser.navigate", status.Url ?? uri.AbsoluteUri, $"ok:{status.Channel}", cancellationToken);
        return status;
    }

    [McpServerTool(Name = "browser_snapshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Returns a bounded DOM/semantic snapshot of the active dedicated browser page, including role/name/text/label/test-id, geometry, selected computed styles, viewport, and redacted form values. Password values are never returned.")]
    public async Task<BrowserSnapshot> Snapshot(
        [Description("Maximum elements returned, clamped to 1..1500.")] int maxElements = 500,
        CancellationToken cancellationToken = default)
    {
        await RequireApprovalAsync("browser.view", "dom-inspection", "Inspect the active browser DOM, accessible names, geometry, selected styles, and non-password form values.", cancellationToken);
        EnsureComputerUseAvailable();
        var snapshot = await _browser.SnapshotAsync(maxElements, cancellationToken);
        _computerUse.Touch("browser-view", snapshot.Url);
        await audit.WriteAsync("browser.snapshot", snapshot.Url, $"ok:{snapshot.Elements.Count}:truncated={snapshot.Truncated}", cancellationToken);
        return snapshot;
    }

    [McpServerTool(Name = "browser_click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Clicks exactly one DOM element selected semantically by CSS, role, accessible name, text, label, test id and optional index. Ambiguous selectors fail without clicking.")]
    public async Task<BrowserActionResult> Click(
        string? css = null, string? role = null, string? name = null, string? text = null,
        string? label = null, string? testId = null, int? index = null,
        CancellationToken cancellationToken = default)
    {
        var selector = Selector(css, role, name, text, label, testId, index);
        await RequireApprovalAsync("browser.input", "semantic-action", $"Click browser element selected by {Describe(selector)}.", cancellationToken);
        EnsureComputerUseAvailable();
        var result = await _browser.ClickAsync(selector, cancellationToken);
        _computerUse.Touch("browser-input", $"click:{result.Role}:{result.Name}");
        await audit.WriteAsync("browser.click", Describe(selector), $"ok:{result.Role}:{Trim(result.Name)}", cancellationToken);
        return result;
    }

    [McpServerTool(Name = "browser_fill", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Replaces text in exactly one DOM form/contenteditable element selected semantically. Password fields are refused. Literal text is omitted from approvals and audit logs.")]
    public async Task<BrowserActionResult> Fill(
        [Description("Text to enter. This literal value is intentionally omitted from MateMCP approval/audit details.")] string value,
        string? css = null, string? role = null, string? name = null, string? text = null,
        string? label = null, string? testId = null, int? index = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var selector = Selector(css, role, name, text, label, testId, index);
        await RequireApprovalAsync("browser.input", "semantic-action", $"Fill browser element selected by {Describe(selector)} with {value.Length} characters (text omitted).", cancellationToken);
        EnsureComputerUseAvailable();
        var result = await _browser.FillAsync(selector, value, cancellationToken);
        _computerUse.Touch("browser-input", $"fill:{result.Role}:{result.Name}");
        await audit.WriteAsync("browser.fill", Describe(selector), $"ok:length:{value.Length}:{result.Role}", cancellationToken);
        return result;
    }

    [McpServerTool(Name = "browser_screenshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Captures the active dedicated browser viewport/page as an inspectable PNG MCP image. Screenshot pixels are not persisted by MateMCP.")]
    public async Task<IEnumerable<ContentBlock>> Screenshot(
        [Description("Capture beyond the current viewport where Chromium supports it.")] bool fullPage = false,
        CancellationToken cancellationToken = default)
    {
        await RequireApprovalAsync("browser.view", "visual-inspection", $"Capture {(fullPage ? "the full browser page" : "the browser viewport")} as pixels.", cancellationToken);
        EnsureComputerUseAvailable();
        var shot = await _browser.ScreenshotAsync(fullPage, cancellationToken);
        _computerUse.Touch("browser-view", shot.Url);
        await audit.WriteAsync("browser.screenshot", shot.Url, $"ok:{shot.Bytes.Length}:full={fullPage}", cancellationToken);
        var metadata = JsonSerializer.Serialize(new
        {
            shot.Url,
            shot.Title,
            shot.Viewport.Width,
            shot.Viewport.Height,
            shot.Viewport.DeviceScaleFactor,
            bytes = shot.Bytes.Length,
            fullPage
        });
        return
        [
            new TextContentBlock { Text = metadata },
            ImageContentBlock.FromBytes(shot.Bytes, shot.MimeType)
        ];
    }

    [McpServerTool(Name = "browser_set_viewport", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Overrides the dedicated browser viewport/device scale for responsive frontend inspection.")]
    public async Task<BrowserSessionStatus> SetViewport(
        int width,
        int height,
        double deviceScaleFactor = 1,
        CancellationToken cancellationToken = default)
    {
        await RequireApprovalAsync("browser.input", "viewport", $"Set browser viewport to {width}x{height} at device scale {deviceScaleFactor:0.##}.", cancellationToken);
        EnsureComputerUseAvailable();
        var status = await _browser.SetViewportAsync(width, height, deviceScaleFactor, cancellationToken);
        _computerUse.Touch("browser-layout", $"{status.Viewport?.Width}x{status.Viewport?.Height}");
        await audit.WriteAsync("browser.viewport", status.Url ?? "browser", $"ok:{status.Viewport?.Width}x{status.Viewport?.Height}:{status.Viewport?.DeviceScaleFactor}", cancellationToken);
        return status;
    }

    [McpServerTool(Name = "browser_reload", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Reloads the current page in MateMCP's dedicated browser session and waits until the DOM is interactive.")]
    public async Task<BrowserSessionStatus> Reload(CancellationToken cancellationToken = default)
    {
        await RequireApprovalAsync("browser.navigate", "navigation", "Reload the active browser page.", cancellationToken);
        EnsureComputerUseAvailable();
        var status = await _browser.ReloadAsync(cancellationToken);
        _computerUse.Touch("browser-navigation", status.Url);
        await audit.WriteAsync("browser.reload", status.Url ?? "browser", "ok", cancellationToken);
        return status;
    }

    [McpServerTool(Name = "browser_close", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Closes only the temporary-profile browser process owned by MateMCP and deletes its temporary profile on a best-effort basis.")]
    public async Task<string> Close(CancellationToken cancellationToken = default)
    {
        await _browser.CloseAsync(cancellationToken);
        await audit.WriteAsync("browser.close", "dedicated-browser", "ok", cancellationToken);
        return "closed";
    }

    private static BrowserSelector Selector(string? css, string? role, string? name, string? text, string? label, string? testId, int? index)
        => new(css, role, name, text, label, testId, index);

    private async Task RequireApprovalAsync(string capability, string target, string summary, CancellationToken cancellationToken)
    {
        var decision = await approvals.RequestAsync(capability, target, summary, cancellationToken);
        if (decision == ApprovalDecision.Deny)
        {
            await audit.WriteAsync(capability, target, "denied:approval", cancellationToken);
            throw new McpException("Browser action denied by the local user.");
        }
        if (decision == ApprovalDecision.Timeout)
        {
            await audit.WriteAsync(capability, target, "denied:approval-timeout", CancellationToken.None);
            throw new McpException("Browser action approval timed out.");
        }
    }

    private void EnsureComputerUseAvailable()
    {
        try { _computerUse.EnsureAvailable(); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    private static string Describe(BrowserSelector selector)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(selector.Css)) parts.Add($"css={Trim(selector.Css)}");
        if (!string.IsNullOrWhiteSpace(selector.Role)) parts.Add($"role={Trim(selector.Role)}");
        if (!string.IsNullOrWhiteSpace(selector.Name)) parts.Add($"name={Trim(selector.Name)}");
        if (!string.IsNullOrWhiteSpace(selector.Text)) parts.Add($"text={Trim(selector.Text)}");
        if (!string.IsNullOrWhiteSpace(selector.Label)) parts.Add($"label={Trim(selector.Label)}");
        if (!string.IsNullOrWhiteSpace(selector.TestId)) parts.Add($"testId={Trim(selector.TestId)}");
        if (selector.Index is not null) parts.Add($"index={selector.Index}");
        return string.Join(",", parts);
    }

    private static string Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = value.Trim();
        return value.Length <= 120 ? value : value[..120] + "…";
    }
}
