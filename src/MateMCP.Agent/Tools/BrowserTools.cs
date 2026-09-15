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
public sealed class BrowserTools(
    ApprovalService approvals,
    AuditLog audit,
    ICredentialStore secrets,
    CredentialInjectionRateLimiter injectionRateLimiter)
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
        await RequireRiskApprovalAsync("browser.navigate", "navigation", "navigate", null, $"Open or navigate the dedicated browser to {uri.AbsoluteUri}.", cancellationToken);
        EnsureComputerUseAvailable();
        var status = await _browser.OpenAsync(uri.AbsoluteUri, channel, cancellationToken);
        _computerUse.Touch("browser-navigation", status.Url);
        await audit.WriteAsync("browser.navigate", status.Url ?? uri.AbsoluteUri, $"ok:{status.Channel}", cancellationToken);
        return status;
    }

    [McpServerTool(Name = "browser_snapshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Returns a bounded DOM/semantic snapshot of the active dedicated browser page, including role/name/text/label/test-id, geometry, selected computed styles, viewport, and redacted form values. Password and Secret Manager-injected values are never returned.")]
    public async Task<BrowserSnapshot> Snapshot(
        [Description("Maximum elements returned, clamped to 1..1500.")] int maxElements = 500,
        CancellationToken cancellationToken = default)
    {
        if (SensitiveUiGuard.Shared.Active) throw new McpException(SensitiveUiGuard.CaptureBlockedMessage);
        await RequireRiskApprovalAsync("browser.view", "dom-inspection", "view", null, "Inspect the active browser DOM, accessible names, geometry, selected styles, and non-password form values.", cancellationToken);
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
        await RequireRiskApprovalAsync("browser.input", "semantic-action", "invoke", selector, $"Click browser element selected by {Describe(selector)}.", cancellationToken);
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
        await RequireRiskApprovalAsync("browser.input", "semantic-action", "fill", selector, $"Fill browser element selected by {Describe(selector)} with {value.Length} characters (text omitted).", cancellationToken);
        EnsureComputerUseAvailable();
        var result = await _browser.FillAsync(selector, value, cancellationToken);
        _computerUse.Touch("browser-input", $"fill:{result.Role}:{result.Name}");
        await audit.WriteAsync("browser.fill", Describe(selector), $"ok:length:{value.Length}:{result.Role}", cancellationToken);
        return result;
    }

    [McpServerTool(Name = "browser_fill_secret", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Fills one uniquely selected dedicated-browser text/password input with a named Secret Manager credential. The plaintext is resolved inside the Agent and never appears in MCP arguments/results, approvals, or audit. The exact DOM element is bound before approval. Ordinary text inputs are visually masked and their DOM value is redacted until overwritten or removed.")]
    public async Task<object> FillSecret(
        string credential,
        string? css = null, string? role = null, string? name = null, string? text = null,
        string? label = null, string? testId = null, int? index = null,
        CancellationToken cancellationToken = default)
    {
        var selector = Selector(css, role, name, text, label, testId, index);
        var available = await secrets.ListAsync(cancellationToken);
        var info = available.FirstOrDefault(x => string.Equals(x.Name, credential, StringComparison.OrdinalIgnoreCase));
        if (info is null) throw new McpException($"Named credential '{credential}' does not exist.");
        var auditTarget = $"browser:{Describe(selector)}";
        if (!info.IsAllowedForTool(UserSecretInfo.BrowserFillSecretTool))
        {
            await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.BrowserFillSecretTool, auditTarget, "denied:tool-policy", cancellationToken);
            throw new McpException($"Credential '{info.Name}' is not authorized for tool '{UserSecretInfo.BrowserFillSecretTool}'.");
        }
        if (!injectionRateLimiter.TryAcquire(info.Name, out var retryAfter))
        {
            await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.BrowserFillSecretTool, auditTarget, "denied:rate-limit", cancellationToken);
            throw new McpException($"Credential injection rate limit exceeded. Retry after {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} seconds.");
        }

        EnsureComputerUseAvailable();
        BrowserSecretTarget target;
        try { target = await _browser.BindSecretTargetAsync(selector, cancellationToken); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }

        string? value = null;
        try
        {
            var assessment = new ComputerUseRiskAssessment(
                ComputerUseRiskLevel.High,
                "Enters a locally stored credential into the selected browser field without revealing the secret value to the AI.",
                "Using a credential in a browser is security-sensitive even though MateMCP keeps the plaintext Agent-local.");
            var decision = await approvals.RequestComputerUseAsync(
                "secret.use",
                $"{info.Name}@{auditTarget}",
                $"Use credential {info.Name} in browser {target.Role} '{target.Name ?? "unnamed"}'. Secret value omitted.",
                assessment,
                cancellationToken);
            if (decision == ApprovalDecision.Deny)
            {
                await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.BrowserFillSecretTool, auditTarget, "denied:approval", cancellationToken);
                throw new McpException("Credential use denied by local user.");
            }
            if (decision == ApprovalDecision.Timeout)
            {
                await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.BrowserFillSecretTool, auditTarget, "denied:approval-timeout", cancellationToken);
                throw new McpException("Credential use approval timed out.");
            }

            value = await secrets.ResolveAsync(info.Name, cancellationToken);
            if (value is null) throw new McpException($"Named credential '{info.Name}' could not be resolved from the local secure store.");
            BrowserActionResult result;
            try { result = await _browser.FillBoundSecretAsync(target, value, cancellationToken); }
            catch (InvalidOperationException ex)
            {
                await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.BrowserFillSecretTool, auditTarget, "failed:target-or-browser", cancellationToken);
                throw new McpException(ex.Message);
            }

            _computerUse.Touch("browser-input", $"secret-fill:{result.Role}:{result.Name}");
            await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.BrowserFillSecretTool, auditTarget, "injected", cancellationToken);
            return new
            {
                credential = info.Name,
                injected = true,
                target = new { result.Role, result.Name, Protected = result.Protected == true }
            };
        }
        finally
        {
            value = null;
            try { await _browser.ReleaseSecretBindingAsync(target.BindingId, CancellationToken.None); } catch { }
        }
    }

    [McpServerTool(Name = "browser_screenshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Captures the active dedicated browser viewport/page as an inspectable PNG MCP image. Screenshot pixels are not persisted by MateMCP.")]
    public async Task<IEnumerable<ContentBlock>> Screenshot(
        [Description("Capture beyond the current viewport where Chromium supports it.")] bool fullPage = false,
        CancellationToken cancellationToken = default)
    {
        if (SensitiveUiGuard.Shared.Active) throw new McpException(SensitiveUiGuard.CaptureBlockedMessage);
        await RequireRiskApprovalAsync("browser.view", "visual-inspection", "view", null, $"Capture {(fullPage ? "the full browser page" : "the browser viewport")} as pixels.", cancellationToken);
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
        await RequireRiskApprovalAsync("browser.input", "viewport", "view", null, $"Set browser viewport to {width}x{height} at device scale {deviceScaleFactor:0.##}.", cancellationToken);
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
        await RequireRiskApprovalAsync("browser.navigate", "navigation", "reload", null, "Reload the active browser page.", cancellationToken);
        EnsureComputerUseAvailable();
        var status = await _browser.ReloadAsync(cancellationToken);
        _computerUse.Touch("browser-navigation", status.Url);
        await audit.WriteAsync("browser.reload", status.Url ?? "browser", "ok", cancellationToken);
        return status;
    }

    [McpServerTool(Name = "browser_back", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Navigates the dedicated browser to the previous history entry and waits for the page to become interactive.")]
    public async Task<BrowserSessionStatus> Back(CancellationToken cancellationToken = default)
    {
        await RequireRiskApprovalAsync("browser.navigate", "navigation", "back", null, "Navigate the dedicated browser back one history entry.", cancellationToken);
        EnsureComputerUseAvailable();
        var status = await _browser.BackAsync(cancellationToken);
        _computerUse.Touch("browser-navigation", status.Url);
        await audit.WriteAsync("browser.back", status.Url ?? "browser", "ok", cancellationToken);
        return status;
    }

    [McpServerTool(Name = "browser_forward", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Navigates the dedicated browser to the next history entry and waits for the page to become interactive.")]
    public async Task<BrowserSessionStatus> Forward(CancellationToken cancellationToken = default)
    {
        await RequireRiskApprovalAsync("browser.navigate", "navigation", "forward", null, "Navigate the dedicated browser forward one history entry.", cancellationToken);
        EnsureComputerUseAvailable();
        var status = await _browser.ForwardAsync(cancellationToken);
        _computerUse.Touch("browser-navigation", status.Url);
        await audit.WriteAsync("browser.forward", status.Url ?? "browser", "ok", cancellationToken);
        return status;
    }

    [McpServerTool(Name = "browser_diagnostics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Returns bounded recent console warnings/errors and JavaScript page exceptions from the dedicated browser. Console text can contain application data; diagnostic text is not copied into MateMCP audit details.")]
    public async Task<BrowserDiagnostics> Diagnostics(
        int maxEntries = 100,
        bool includeInfo = false,
        bool clear = true,
        CancellationToken cancellationToken = default)
    {
        await RequireRiskApprovalAsync("browser.view", "browser-diagnostics", "view", null, "Read recent browser console/page diagnostics. Diagnostic text will not be persisted in the audit log.", cancellationToken);
        EnsureComputerUseAvailable();
        var result = await _browser.GetDiagnosticsAsync(maxEntries, includeInfo, clear, cancellationToken);
        _computerUse.Touch("browser-view", "diagnostics");
        await audit.WriteAsync("browser.diagnostics", "dedicated-browser", $"ok:entries={result.Entries.Count}:truncated={result.Truncated}", cancellationToken);
        return result;
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

    private async Task RequireRiskApprovalAsync(
        string capability,
        string baseTarget,
        string action,
        BrowserSelector? selector,
        string summary,
        CancellationToken cancellationToken)
    {
        var semanticTarget = selector is null ? baseTarget : Describe(selector);
        var assessment = selector is null
            ? ComputerUseRiskClassifier.AssessSemantic(action)
            : ComputerUseRiskClassifier.AssessSemantic(
                action,
                selector.Role,
                selector.Name ?? selector.Label ?? selector.Text,
                selector.TestId ?? selector.Css);
        var target = ComputerUseRiskClassifier.PolicyTarget(baseTarget, action, semanticTarget, assessment);
        var decision = await approvals.RequestComputerUseAsync(capability, target, summary, assessment, cancellationToken);
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
