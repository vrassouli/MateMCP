using System.ComponentModel;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Desktop;
using MateMCP.Agent.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class DesktopInputTools(
    ApprovalService approvals,
    AuditLog audit,
    AgentActivityGate? activity = null)
{
    private readonly DesktopInputService _input = new();
    private readonly DesktopVisionService _vision = new();
    private readonly AgentActivityGate _activity = activity ?? new AgentActivityGate();
    private readonly ComputerUseSessionManager _computerUse = ComputerUseSessionManager.Shared;

    [McpServerTool(Name = "mouse_move", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Moves the local mouse pointer to global desktop logical coordinates. Raw desktop input is approval-gated because coordinate actions do not carry semantic intent.")]
    public async Task<string> MouseMove(int x, int y, CancellationToken cancellationToken = default)
    {
        using var lease = EnterActivity();
        var summary = $"Move pointer to ({x},{y}).";
        await AuthorizeInputAsync(summary, cancellationToken);
        RunInput(() => _input.MoveMouse(x, y));
        await audit.WriteAsync("desktop.input.mouse", $"move:{x},{y}", "ok", cancellationToken);
        return "moved";
    }

    [McpServerTool(Name = "mouse_click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Moves to the requested global desktop coordinates and clicks the specified mouse button. Use screen/window capture to observe the target first. Raw clicks are approval-gated because their semantic effect is not known to the Agent.")]
    public async Task<string> MouseClick(
        int x,
        int y,
        string button = "left",
        int clickCount = 1,
        CancellationToken cancellationToken = default)
    {
        using var lease = EnterActivity();
        button = DesktopInputService.NormalizeButton(button);
        clickCount = Math.Clamp(clickCount, 1, 3);
        var summary = $"{button} click x{clickCount} at ({x},{y}).";
        await AuthorizeInputAsync(summary, cancellationToken);
        RunInput(() => _input.ClickMouse(x, y, button, clickCount));
        await audit.WriteAsync("desktop.input.mouse", $"click:{button}:{clickCount}@{x},{y}", "ok", cancellationToken);
        return "clicked";
    }

    [McpServerTool(Name = "mouse_drag", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Drags from one global desktop point to another. Raw drag actions are approval-gated.")]
    public async Task<string> MouseDrag(
        int fromX,
        int fromY,
        int toX,
        int toY,
        string button = "left",
        int durationMs = 350,
        CancellationToken cancellationToken = default)
    {
        using var lease = EnterActivity();
        button = DesktopInputService.NormalizeButton(button);
        durationMs = Math.Clamp(durationMs, 0, 5000);
        var summary = $"Drag {button} from ({fromX},{fromY}) to ({toX},{toY}) over {durationMs} ms.";
        await AuthorizeInputAsync(summary, cancellationToken);
        RunInput(() => _input.DragMouse(fromX, fromY, toX, toY, button, durationMs));
        await audit.WriteAsync("desktop.input.mouse", $"drag:{button}:{fromX},{fromY}->{toX},{toY}", "ok", cancellationToken);
        return "dragged";
    }

    [McpServerTool(Name = "mouse_scroll", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Scrolls the desktop by horizontal/vertical deltas, optionally after moving to a global desktop coordinate. Positive/negative direction follows the host OS event convention.")]
    public async Task<string> MouseScroll(
        int deltaY,
        int deltaX = 0,
        int? x = null,
        int? y = null,
        CancellationToken cancellationToken = default)
    {
        using var lease = EnterActivity();
        if ((x is null) != (y is null)) throw new McpException("x and y must either both be supplied or both be omitted.");
        var summary = $"Scroll deltaX={deltaX}, deltaY={deltaY}" + (x is null ? "." : $" at ({x},{y}).");
        await AuthorizeInputAsync(summary, cancellationToken);
        RunInput(() => _input.ScrollMouse(deltaX, deltaY, x, y));
        await audit.WriteAsync("desktop.input.mouse", $"scroll:{deltaX},{deltaY}", "ok", cancellationToken);
        return "scrolled";
    }

    [McpServerTool(Name = "keyboard_type", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Types literal Unicode text into the currently focused control. The text itself is never written to MateMCP audit logs. Do not use this tool for passwords/secrets; future secret GUI injection will use a non-disclosing secret path.")]
    public async Task<string> KeyboardType(string text, CancellationToken cancellationToken = default)
    {
        using var lease = EnterActivity();
        ArgumentNullException.ThrowIfNull(text);
        var summary = $"Type {text.Length} literal characters into the focused control. Text is intentionally omitted from the approval/audit detail.";
        await AuthorizeInputAsync(summary, cancellationToken);
        RunInput(() => _input.TypeText(text));
        await audit.WriteAsync("desktop.input.keyboard", "type", $"ok:length:{text.Length}", cancellationToken);
        return "typed";
    }

    [McpServerTool(Name = "keyboard_press", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Presses and releases one named key (for example ENTER, ESC, TAB, DELETE, LEFT, F5, A, 1).")]
    public async Task<string> KeyboardPress(string key, CancellationToken cancellationToken = default)
    {
        using var lease = EnterActivity();
        key = DesktopInputService.NormalizeKey(key);
        var summary = $"Press key {key}.";
        await AuthorizeInputAsync(summary, cancellationToken);
        RunInput(() => _input.PressKey(key));
        await audit.WriteAsync("desktop.input.keyboard", $"key:{key}", "ok", cancellationToken);
        return "pressed";
    }

    [McpServerTool(Name = "keyboard_shortcut", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Presses a key combination in order, then releases it in reverse order. Supports modifier-only combinations such as ALT+SHIFT as well as CTRL/CMD+A and similar shortcuts.")]
    public async Task<string> KeyboardShortcut(string[] keys, CancellationToken cancellationToken = default)
    {
        using var lease = EnterActivity();
        if (keys is null) throw new McpException("keys is required.");
        var normalized = keys.Select(DesktopInputService.NormalizeKey).ToArray();
        var summary = $"Press shortcut {string.Join('+', normalized)}.";
        await AuthorizeInputAsync(summary, cancellationToken);
        RunInput(() => _input.PressShortcut(normalized));
        await audit.WriteAsync("desktop.input.keyboard", $"shortcut:{string.Join('+', normalized)}", "ok", cancellationToken);
        return "pressed";
    }

    [McpServerTool(Name = "window_focus", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Activates/focuses a visible window returned by window_list. On macOS the owning application is activated; on Windows the selected HWND is restored/activated subject to OS foreground restrictions.")]
    public async Task<string> WindowFocus(string windowId, CancellationToken cancellationToken = default)
    {
        using var lease = EnterActivity();
        EnsureComputerUseAvailable();
        var windows = await _vision.ListWindowsAsync(cancellationToken);
        var window = windows.FirstOrDefault(item => string.Equals(item.Id, windowId, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"Window '{windowId}' is no longer available. Call window_list again.");
        var summary = $"Activate window '{Trim(window.Title)}' owned by {Trim(window.Application)}.";
        await AuthorizeInputAsync(summary, cancellationToken);
        RunInput(() => _input.FocusWindow(window.Id, window.ProcessId));
        await audit.WriteAsync("desktop.input.window", $"focus:{window.Application}:{window.Id}", "ok", cancellationToken);
        return "focused";
    }

    private async Task AuthorizeInputAsync(string summary, CancellationToken cancellationToken)
    {
        EnsureComputerUseAvailable();
        var decision = await approvals.RequestAsync("desktop.input", "raw-input", summary, cancellationToken);
        if (decision == ApprovalDecision.Deny)
        {
            await audit.WriteAsync("desktop.input", "raw-input", "denied:approval", cancellationToken);
            throw new McpException("Desktop input denied by the local user.");
        }
        if (decision == ApprovalDecision.Timeout)
        {
            await audit.WriteAsync("desktop.input", "raw-input", "denied:approval-timeout", CancellationToken.None);
            throw new McpException("Desktop input approval timed out.");
        }
        try { _computerUse.Touch("input", summary); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    private void EnsureComputerUseAvailable()
    {
        try { _computerUse.EnsureAvailable(); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }


    private static void RunInput(Action action)
    {
        try { action(); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    private IDisposable EnterActivity()
    {
        if (!_activity.TryEnter(out var lease) || lease is null)
            throw new McpException("MateMCP Agent is preparing a verified Desktop update. Retry after the Agent restarts.");
        return lease;
    }

    private static string Trim(string value) => value.Length <= 120 ? value : value[..120] + "…";
}
