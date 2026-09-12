using System.Diagnostics;
using System.Text.Json;

namespace MateMCP.Agent.Desktop;

public sealed class MacSemanticUiActionService
{
    private readonly DesktopVisionService _vision = new();
    private readonly SemanticUiService _semantic = new();

    public async Task<UiElementInfo> ActAsync(
        string windowId,
        UiSelector selector,
        string action,
        string? text = null,
        bool? expanded = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Native macOS semantic actions are only available on macOS.");

        var before = await _semantic.SnapshotAsync(windowId, 1000, cancellationToken);
        var selected = UiSelectorResolver.Resolve(before.Elements, selector);
        if (action == "value" && selected.Protected)
            throw new InvalidOperationException("MateMCP refuses to read or replace protected/secure-text values through semantic UI automation.");

        var window = await ResolveWindowAsync(windowId, cancellationToken);
        var script = BuildActionScript(window.ProcessId, window.Title, selected.Id, action, text, expanded);
        await RunScriptAsync(script, selected, action, cancellationToken);

        try
        {
            var after = await _semantic.SnapshotAsync(windowId, 1000, cancellationToken);
            return after.Elements.FirstOrDefault(element => string.Equals(element.Id, selected.Id, StringComparison.Ordinal))
                ?? selected;
        }
        catch (InvalidOperationException)
        {
            return selected;
        }
    }

    private async Task<DesktopWindowInfo> ResolveWindowAsync(string windowId, CancellationToken cancellationToken)
    {
        var windows = await _vision.ListWindowsAsync(cancellationToken);
        return windows.FirstOrDefault(window => string.Equals(window.Id, windowId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Window '{windowId}' is no longer available. Call window_list again.");
    }

    private static string BuildActionScript(
        int processId,
        string title,
        string elementId,
        string action,
        string? text,
        bool? expanded)
    {
        if (!elementId.StartsWith("ax:0", StringComparison.Ordinal))
            throw new InvalidOperationException($"Element '{elementId}' is not a macOS Accessibility path.");

        var tail = elementId[4..];
        var indexes = string.IsNullOrEmpty(tail)
            ? Array.Empty<int>()
            : tail.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part, out var index) && index >= 0
                    ? index
                    : throw new InvalidOperationException($"Element '{elementId}' contains an invalid Accessibility path."))
                .ToArray();

        var titleJson = JsonSerializer.Serialize(title);
        var actionJson = JsonSerializer.Serialize(action);
        var textJson = JsonSerializer.Serialize(text ?? string.Empty);
        var pathJson = JsonSerializer.Serialize(indexes);
        var expandedJson = expanded is null ? "null" : expanded.Value ? "true" : "false";

        return $$"""
        var se = Application('System Events');
        var ps = se.processes.whose({unixId: {{processId}}})();
        if (!ps || ps.length === 0) throw new Error('Target process is not available to Accessibility.');
        var p = ps[0];
        var wins = p.windows();
        if (!wins || wins.length === 0) throw new Error('Target process has no accessible windows.');
        var wanted = {{titleJson}};
        var root = null;
        for (var wi = 0; wi < wins.length; wi++) {
          var n = null; try { n = wins[wi].name(); } catch (e) {}
          if (n === wanted) { root = wins[wi]; break; }
        }
        if (root === null) root = wins[0];

        var path = {{pathJson}};
        var e = root;
        for (var pi = 0; pi < path.length; pi++) {
          var children = e.uiElements();
          var index = path[pi];
          if (!children || index < 0 || index >= children.length)
            throw new Error('The selected Accessibility element is no longer available. Take a new ui_snapshot and retry.');
          e = children[index];
        }

        var action = {{actionJson}};
        var text = {{textJson}};
        var expanded = {{expandedJson}};
        function performNamed(name) {
          var acts = [];
          try { acts = e.actions(); } catch (err) {}
          for (var ai = 0; ai < acts.length; ai++) {
            var n = null; try { n = acts[ai].name(); } catch (err) {}
            if (n === name) { acts[ai].perform(); return true; }
          }
          return false;
        }

        if (action === 'focus') {
          e.focused = true;
        } else if (action === 'value') {
          var role = null; try { role = e.role(); } catch (err) {}
          if (role === 'AXSecureTextField') throw new Error('MateMCP refuses to replace protected/secure-text values.');
          e.value = text;
        } else if (action === 'invoke' || action === 'toggle') {
          if (!performNamed('AXPress')) throw new Error("The selected control does not expose native AXPress.");
        } else if (action === 'select') {
          if (!performNamed('AXPress')) {
            try { e.selected = true; }
            catch (err) { throw new Error("The selected control exposes neither AXPress nor a writable selected property."); }
          }
        } else if (action === 'expand' || action === 'collapse') {
          try { e.expanded = !!expanded; }
          catch (err) { throw new Error("The selected control does not expose a writable expanded property."); }
        } else if (action === 'scroll') {
          if (!performNamed('AXScrollToVisible')) throw new Error("The selected control does not expose native AXScrollToVisible.");
        } else {
          throw new Error('Unknown semantic action: ' + action);
        }
        'ok';
        """;
    }

    private static async Task RunScriptAsync(
        string script,
        UiElementInfo selected,
        string action,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("/usr/bin/osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("JavaScript");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start macOS Accessibility action.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("macOS Accessibility action timed out. Verify Accessibility permission for MateMCP.");
        }

        _ = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            var bounds = selected.Bounds is null
                ? "No coordinate fallback bounds are available."
                : $"Explicit coordinate fallback bounds: x={selected.Bounds.X}, y={selected.Bounds.Y}, width={selected.Bounds.Width}, height={selected.Bounds.Height}.";
            throw new InvalidOperationException(
                $"macOS Accessibility could not perform '{action}' on {selected.Role} '{selected.Name ?? selected.Id}': {Trim(stderr, 500)} {bounds} Use raw input only as an explicit fallback.");
        }
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value.Trim() : value[..max].Trim() + "…";
}
