using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Desktop;
using MateMCP.Agent.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class DesktopSemanticTools(
    ApprovalService approvals,
    AuditLog audit,
    ComputerUsePreviewService preview,
    ICredentialStore secrets,
    CredentialInjectionRateLimiter injectionRateLimiter)
{
    private readonly SemanticUiService _semantic = new();
    private readonly MacSemanticUiActionService _macSemantic = new();
    private readonly ComputerUseSessionManager _computerUse = ComputerUseSessionManager.Shared;

    [McpServerTool(Name = "ui_snapshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the native accessibility/UI Automation tree for a visible window. Values from password/secure-text controls are always redacted. Prefer this semantic snapshot before raw coordinate input.")]
    public async Task<UiSnapshot> Snapshot(
        [Description("Window id returned by window_list.")] string windowId,
        [Description("Maximum number of accessibility elements to return, clamped to 1..1000.")] int maxElements = 400,
        CancellationToken cancellationToken = default)
    {
        await AuthorizeViewAsync($"Inspect the native accessibility tree for window id {windowId}.", cancellationToken);
        UiSnapshot snapshot;
        try { snapshot = await _semantic.SnapshotAsync(windowId, maxElements, cancellationToken); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
        _computerUse.Touch("semantic-view", $"window {windowId}");
        await preview.TrackWindowAsync(windowId, action: "inspect", cancellationToken: cancellationToken);
        await audit.WriteAsync("desktop.semantic.snapshot", windowId, $"ok:{snapshot.Elements.Count}:truncated={snapshot.Truncated}", cancellationToken);
        return snapshot;
    }

    [McpServerTool(Name = "ui_click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Invokes a uniquely matched native UI control by semantic selector. If the selector is ambiguous, nothing is invoked. If the control lacks a native semantic action, the tool fails and returns bounds for an explicit raw-input fallback when available.")]
    public async Task<UiElementInfo> Click(
        string windowId, string? role = null, string? name = null, string? automationId = null,
        string? parentId = null, int? index = null, CancellationToken cancellationToken = default)
        => await ActAsync("invoke", windowId, Selector(role, name, automationId, parentId, index), null, cancellationToken);

    [McpServerTool(Name = "ui_type", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Replaces the value of a uniquely matched native editable control using the native accessibility value API. Secure/password controls are refused. Literal text is not written to MateMCP approval/audit logs.")]
    public async Task<UiElementInfo> Type(
        string windowId, string text, string? role = null, string? name = null, string? automationId = null,
        string? parentId = null, int? index = null, CancellationToken cancellationToken = default)
        => await ActAsync("value", windowId, Selector(role, name, automationId, parentId, index), text, cancellationToken);

    [McpServerTool(Name = "ui_fill_secret", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Safely fills a uniquely matched browser/native text or password field with a locally stored MateMCP credential. Pass only the credential name; the plaintext is resolved inside the Agent and is never returned to the AI, approvals, logs, audit, or tool output. The target is bound before approval and revalidated by exact accessibility element id immediately before injection. If the target is an ordinary unmasked textbox, MateMCP temporarily suppresses visual/DOM capture and redacts semantic values until the field is cleared or disappears.")]
    public async Task<object> FillSecret(
        string windowId,
        string credential,
        string? role = null,
        string? name = null,
        string? automationId = null,
        string? parentId = null,
        int? index = null,
        CancellationToken cancellationToken = default)
    {
        EnsureComputerUseAvailable();
        var selector = Selector(role, name, automationId, parentId, index);
        var available = await secrets.ListAsync(cancellationToken);
        var info = available.FirstOrDefault(x => string.Equals(x.Name, credential, StringComparison.OrdinalIgnoreCase));
        if (info is null) throw new McpException($"Named credential '{credential}' does not exist.");

        UiElementInfo selected;
        try { selected = await _semantic.ResolveAsync(windowId, selector, cancellationToken); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }

        var targetDescription = DescribeTarget(selected);
        var targetFingerprint = TargetFingerprint(windowId, selected);
        var auditTarget = $"ui:{targetFingerprint}";
        if (!info.IsAllowedForTool(UserSecretInfo.UiFillSecretTool))
        {
            await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.UiFillSecretTool, auditTarget, "denied:tool-policy", cancellationToken);
            throw new McpException($"Credential '{info.Name}' is not authorized for tool '{UserSecretInfo.UiFillSecretTool}'.");
        }
        if (!injectionRateLimiter.TryAcquire(info.Name, out var retryAfter))
        {
            await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.UiFillSecretTool, auditTarget, "denied:rate-limit", cancellationToken);
            throw new McpException($"Credential injection rate limit exceeded. Retry after {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} seconds.");
        }
        if (!selected.Enabled) throw new McpException("The selected UI control is disabled.");
        if (selected.Role is not ("textbox" or "password"))
            throw new McpException("ui_fill_secret requires an editable text/password control.");

        var assessment = new ComputerUseRiskAssessment(
            ComputerUseRiskLevel.High,
            "Enters a locally stored credential into the selected UI field without revealing the secret value to the AI.",
            "Using a credential in another application is security-sensitive even though MateMCP keeps the plaintext Agent-local.");
        var approvalTarget = $"{info.Name}@{auditTarget}";
        var decision = await approvals.RequestComputerUseAsync(
            "secret.use",
            approvalTarget,
            $"Use credential {info.Name} in {targetDescription} in window {windowId}. Secret value omitted.",
            assessment,
            cancellationToken);
        if (decision == ApprovalDecision.Deny)
        {
            await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.UiFillSecretTool, auditTarget, "denied:approval", cancellationToken);
            throw new McpException("Credential use denied by local user.");
        }
        if (decision == ApprovalDecision.Timeout)
        {
            await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.UiFillSecretTool, auditTarget, "denied:approval-timeout", cancellationToken);
            throw new McpException("Credential use approval timed out.");
        }

        var value = await secrets.ResolveAsync(info.Name, cancellationToken);
        if (value is null) throw new McpException($"Named credential '{info.Name}' could not be resolved from the local secure store.");
        try
        {
            UiElementInfo result;
            try { result = await _semantic.FillSecretAsync(windowId, selected, value, cancellationToken); }
            catch (InvalidOperationException ex)
            {
                await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.UiFillSecretTool, auditTarget, "failed:target-or-platform", cancellationToken);
                throw new McpException(ex.Message);
            }

            _computerUse.Touch("semantic-input", $"secret-value:{result.Role}:{result.Name ?? result.AutomationId ?? result.Id}");
            if (!result.Protected) SensitiveUiGuard.Shared.Mark(windowId, result.Id);
            await audit.WriteCredentialUsageAsync(info.Name, UserSecretInfo.UiFillSecretTool, auditTarget, "injected", cancellationToken);
            return new
            {
                windowId,
                credential = info.Name,
                injected = true,
                target = new { result.Id, result.Role, result.Name, result.AutomationId, result.Protected }
            };
        }
        finally { value = null; }
    }

    [McpServerTool(Name = "ui_focus", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Focuses a uniquely matched native UI control through the accessibility/UI Automation API.")]
    public async Task<UiElementInfo> Focus(
        string windowId, string? role = null, string? name = null, string? automationId = null,
        string? parentId = null, int? index = null, CancellationToken cancellationToken = default)
        => await ActAsync("focus", windowId, Selector(role, name, automationId, parentId, index), null, cancellationToken);

    [McpServerTool(Name = "ui_toggle", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Toggles a uniquely matched native checkbox/toggle control using its native accessibility action.")]
    public async Task<UiElementInfo> Toggle(
        string windowId, string? role = null, string? name = null, string? automationId = null,
        string? parentId = null, int? index = null, CancellationToken cancellationToken = default)
        => await ActAsync("toggle", windowId, Selector(role, name, automationId, parentId, index), null, cancellationToken);

    [McpServerTool(Name = "ui_select", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Selects a uniquely matched native list/tab/tree item using its native accessibility action.")]
    public async Task<UiElementInfo> Select(
        string windowId, string? role = null, string? name = null, string? automationId = null,
        string? parentId = null, int? index = null, CancellationToken cancellationToken = default)
        => await ActAsync("select", windowId, Selector(role, name, automationId, parentId, index), null, cancellationToken);

    [McpServerTool(Name = "ui_expand", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Expands or collapses a uniquely matched native control using its accessibility expand/collapse capability.")]
    public async Task<UiElementInfo> Expand(
        string windowId, bool expanded = true, string? role = null, string? name = null, string? automationId = null,
        string? parentId = null, int? index = null, CancellationToken cancellationToken = default)
    {
        var selector = Selector(role, name, automationId, parentId, index);
        var action = expanded ? "expand" : "collapse";
        await AuthorizeActionAsync(action, windowId, selector, null, cancellationToken);
        UiElementInfo result;
        try
        {
            result = OperatingSystem.IsMacOS()
                ? await _macSemantic.ActAsync(windowId, selector, action, expanded: expanded, cancellationToken: cancellationToken)
                : await _semantic.SetExpandedAsync(windowId, selector, expanded, cancellationToken);
        }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
        return await RecordSuccessAsync(action, windowId, selector, result, cancellationToken);
    }

    [McpServerTool(Name = "ui_click_at", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Clicks by window-relative coordinates without moving the physical pointer. MateMCP hit-tests the native accessibility tree at the virtual cursor position and invokes the nearest actionable control. This is an isolated semantic fallback, not global raw mouse input.")]
    public async Task<UiElementInfo> ClickAt(
        [Description("Window id returned by window_list.")] string windowId,
        [Description("X coordinate in logical points relative to the target window's left edge.")] double x,
        [Description("Y coordinate in logical points relative to the target window's top edge.")] double y,
        CancellationToken cancellationToken = default)
    {
        EnsureComputerUseAvailable();
        var summary = $"Isolated semantic click at window-relative point ({x:0.##},{y:0.##}) in window {windowId}; the physical pointer will not move.";
        var assessment = ComputerUseRiskClassifier.AssessRaw("point-click");
        var policyTarget = $"isolated-point:{assessment.Label.ToLowerInvariant()}:{windowId}:{x:0.##},{y:0.##}";
        var decision = await approvals.RequestComputerUseAsync("desktop.semantic", policyTarget, summary, assessment, cancellationToken);
        await EnsureApprovedAsync(decision, "Isolated semantic click", "desktop.semantic", policyTarget, cancellationToken);

        UiElementInfo result;
        try
        {
            result = OperatingSystem.IsMacOS()
                ? await _macSemantic.ClickAtAsync(windowId, x, y, cancellationToken)
                : OperatingSystem.IsWindows()
                    ? await _semantic.ClickAtAsync(windowId, x, y, cancellationToken)
                    : throw new PlatformNotSupportedException("ui_click_at isolated semantic hit-testing currently supports Windows and macOS.");
        }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }

        _computerUse.Touch("semantic-input", $"isolated-point-invoke:{result.Role}:{result.Name ?? result.AutomationId ?? result.Id}");
        await preview.TrackWindowPointAsync(windowId, x, y, "point-invoke", cancellationToken);
        await audit.WriteAsync("desktop.semantic.action", windowId, $"ok:isolated-point-invoke:x={x:0.##}:y={y:0.##}", cancellationToken);
        return result;
    }

    [McpServerTool(Name = "ui_scroll_into_view", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Scrolls a uniquely matched native control into view using its native accessibility scroll capability.")]
    public async Task<UiElementInfo> ScrollIntoView(
        string windowId, string? role = null, string? name = null, string? automationId = null,
        string? parentId = null, int? index = null, CancellationToken cancellationToken = default)
        => await ActAsync("scroll", windowId, Selector(role, name, automationId, parentId, index), null, cancellationToken);

    private async Task<UiElementInfo> ActAsync(string action, string windowId, UiSelector selector, string? text, CancellationToken cancellationToken)
    {
        await AuthorizeActionAsync(action, windowId, selector, text, cancellationToken);
        UiElementInfo result;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                result = await _macSemantic.ActAsync(windowId, selector, action, text, cancellationToken: cancellationToken);
            }
            else
            {
                result = action switch
                {
                    "invoke" => await _semantic.ClickAsync(windowId, selector, cancellationToken),
                    "value" => await _semantic.TypeAsync(windowId, selector, text ?? string.Empty, cancellationToken),
                    "focus" => await _semantic.FocusAsync(windowId, selector, cancellationToken),
                    "toggle" => await _semantic.ToggleAsync(windowId, selector, cancellationToken),
                    "select" => await _semantic.SelectAsync(windowId, selector, cancellationToken),
                    "scroll" => await _semantic.ScrollIntoViewAsync(windowId, selector, cancellationToken),
                    _ => throw new ArgumentOutOfRangeException(nameof(action))
                };
            }
        }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
        return await RecordSuccessAsync(action, windowId, selector, result, cancellationToken);
    }

    private async Task<UiElementInfo> RecordSuccessAsync(string action, string windowId, UiSelector selector, UiElementInfo result, CancellationToken cancellationToken)
    {
        _computerUse.Touch("semantic-input", $"{action}:{result.Role}:{result.Name ?? result.AutomationId ?? result.Id}");
        if (action == "value") SensitiveUiGuard.Shared.Clear(windowId, result.Id);
        await preview.TrackWindowAsync(windowId, result.Bounds, action, cancellationToken);
        await audit.WriteAsync("desktop.semantic.action", windowId, $"ok:{action}:{Describe(selector)}", cancellationToken);
        return result;
    }

    private async Task AuthorizeViewAsync(string summary, CancellationToken cancellationToken)
    {
        EnsureComputerUseAvailable();
        var assessment = ComputerUseRiskClassifier.AssessSemantic("view");
        var decision = await approvals.RequestComputerUseAsync("desktop.view", "semantic-inspection:low", summary, assessment, cancellationToken);
        await EnsureApprovedAsync(decision, "Semantic UI inspection", "desktop.view", "semantic-inspection:low", cancellationToken);
    }

    private async Task AuthorizeActionAsync(string action, string windowId, UiSelector selector, string? text, CancellationToken cancellationToken)
    {
        EnsureComputerUseAvailable();
        var selectorDescription = Describe(selector);
        var textDetail = action == "value" ? $"; replace with {text?.Length ?? 0} characters (text omitted)" : string.Empty;
        var summary = $"Semantic UI action '{action}' in window {windowId}: {selectorDescription}{textDetail}.";
        var assessment = ComputerUseRiskClassifier.AssessSemantic(action, selector.Role, selector.Name, selector.AutomationId);
        var policyTarget = ComputerUseRiskClassifier.PolicyTarget("semantic-action", action, selectorDescription, assessment);
        var decision = await approvals.RequestComputerUseAsync("desktop.semantic", policyTarget, summary, assessment, cancellationToken);
        await EnsureApprovedAsync(decision, "Semantic UI action", "desktop.semantic", policyTarget, cancellationToken);
    }

    private async Task EnsureApprovedAsync(ApprovalDecision decision, string label, string capability, string target, CancellationToken cancellationToken)
    {
        if (decision == ApprovalDecision.Deny)
        {
            await audit.WriteAsync(capability, target, "denied:approval", cancellationToken);
            throw new McpException($"{label} denied by the local user.");
        }
        if (decision == ApprovalDecision.Timeout)
        {
            await audit.WriteAsync(capability, target, "denied:approval-timeout", CancellationToken.None);
            throw new McpException($"{label} approval timed out.");
        }
    }

    private void EnsureComputerUseAvailable()
    {
        try { _computerUse.EnsureAvailable(); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    private static string DescribeTarget(UiElementInfo element)
    {
        var identity = element.Name ?? element.AutomationId ?? element.Id;
        return $"{element.Role} '{identity}'";
    }

    private static string TargetFingerprint(string windowId, UiElementInfo element)
    {
        var raw = $"{windowId}\n{element.Id}\n{element.Role}\n{element.Name}\n{element.AutomationId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..16];
    }

    private static UiSelector Selector(string? role, string? name, string? automationId, string? parentId, int? index)
    {
        if (string.IsNullOrWhiteSpace(role) && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(parentId))
            throw new McpException("A semantic selector requires at least one of role, name, automationId, or parentId. Use index only to disambiguate an otherwise meaningful selector.");
        if (index is < 0) throw new McpException("index must be zero or greater.");
        return new UiSelector(role, name, automationId, parentId, index);
    }

    private static string Describe(UiSelector selector)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(selector.Role)) parts.Add($"role={selector.Role}");
        if (!string.IsNullOrWhiteSpace(selector.Name)) parts.Add($"name={selector.Name}");
        if (!string.IsNullOrWhiteSpace(selector.AutomationId)) parts.Add($"automationId={selector.AutomationId}");
        if (!string.IsNullOrWhiteSpace(selector.ParentId)) parts.Add($"parentId={selector.ParentId}");
        if (selector.Index is not null) parts.Add($"index={selector.Index}");
        return string.Join(", ", parts);
    }
}
