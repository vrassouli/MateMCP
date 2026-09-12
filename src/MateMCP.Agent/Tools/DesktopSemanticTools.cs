using System.ComponentModel;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Desktop;
using MateMCP.Agent.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class DesktopSemanticTools(ApprovalService approvals, AuditLog audit)
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
        await audit.WriteAsync("desktop.semantic.action", windowId, $"ok:{action}:{Describe(selector)}", cancellationToken);
        return result;
    }

    private async Task AuthorizeViewAsync(string summary, CancellationToken cancellationToken)
    {
        EnsureComputerUseAvailable();
        var decision = await approvals.RequestAsync("desktop.view", "semantic-inspection", summary, cancellationToken);
        await EnsureApprovedAsync(decision, "Semantic UI inspection", "desktop.view", "semantic-inspection", cancellationToken);
    }

    private async Task AuthorizeActionAsync(string action, string windowId, UiSelector selector, string? text, CancellationToken cancellationToken)
    {
        EnsureComputerUseAvailable();
        var textDetail = action == "value" ? $"; replace with {text?.Length ?? 0} characters (text omitted)" : string.Empty;
        var summary = $"Semantic UI action '{action}' in window {windowId}: {Describe(selector)}{textDetail}.";
        var decision = await approvals.RequestAsync("desktop.semantic", "semantic-action", summary, cancellationToken);
        await EnsureApprovedAsync(decision, "Semantic UI action", "desktop.semantic", "semantic-action", cancellationToken);
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
