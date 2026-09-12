using System.ComponentModel;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Browser;
using MateMCP.Agent.Desktop;
using MateMCP.Agent.Security;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class BrowserControlTools(ApprovalService approvals, AuditLog audit)
{
    private readonly BrowserAutomationService _browser = BrowserAutomationService.Shared;
    private readonly ComputerUseSessionManager _computerUse = ComputerUseSessionManager.Shared;

    [McpServerTool(Name = "browser_wait_for", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Waits for a semantic browser element to become present, visible, hidden, or absent. Matching is by role/name/text/label/test-id and optional index; ambiguous selectors do not silently choose an element.")]
    public async Task<BrowserWaitResult> WaitFor(
        [Description("Desired state: present, visible, hidden, or absent.")] string state = "visible",
        string? role = null,
        string? name = null,
        string? text = null,
        string? label = null,
        string? testId = null,
        int? index = null,
        [Description("Maximum wait time in milliseconds, clamped to 250..30000.")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        var selector = new BrowserSelector(null, role, name, text, label, testId, index);
        ValidateSemanticSelector(selector);
        state = NormalizeWaitState(state);
        timeoutMs = Math.Clamp(timeoutMs, 250, 30_000);

        await RequireApprovalAsync(
            "browser.view",
            "dom-wait",
            $"Wait up to {timeoutMs} ms for browser element selected by {Describe(selector)} to become {state}.",
            cancellationToken);
        EnsureComputerUseAvailable();

        var started = Stopwatch.GetTimestamp();
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        BrowserSnapshot? latest = null;
        BrowserElementInfo? resolved = null;
        var matchCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = await _browser.SnapshotAsync(1500, cancellationToken);
            var matches = Match(latest.Elements, selector).ToArray();
            matchCount = matches.Length;
            resolved = Resolve(matches, selector.Index);

            if (IsSatisfied(state, matches, resolved, selector.Index))
                break;

            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"Browser wait timed out after {timeoutMs} ms; selector matched {matchCount} element(s) in last snapshot and did not become {state}.");

            await Task.Delay(100, cancellationToken);
        }

        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _computerUse.Touch("browser-view", latest?.Url);
        await audit.WriteAsync("browser.wait", Describe(selector), $"ok:{state}:matches={matchCount}:elapsedMs={elapsedMs}", cancellationToken);
        return new BrowserWaitResult(
            true,
            state,
            elapsedMs,
            matchCount,
            resolved?.Role,
            resolved?.Name,
            resolved?.Visible,
            latest?.Url);
    }

    [McpServerTool(Name = "browser_select", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Selects a value in exactly one semantic HTML select control. The target is constrained to combobox/listbox roles so this tool cannot silently fill a text field.")]
    public async Task<BrowserActionResult> Select(
        [Description("The option value assigned to the select element.")] string value,
        [Description("Select role: combobox for normal single-select, or listbox for multi/size select.")] string role = "combobox",
        string? name = null,
        string? label = null,
        string? testId = null,
        int? index = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 4096) throw new ArgumentOutOfRangeException(nameof(value), "Browser select value is limited to 4096 characters.");
        role = NormalizeSelectRole(role);
        var selector = new BrowserSelector(null, role, name, null, label, testId, index);
        ValidateSemanticSelector(selector);

        await RequireApprovalAsync(
            "browser.input",
            "semantic-select",
            $"Set browser {role} selected by {Describe(selector)} to value '{Trim(value)}'.",
            cancellationToken);
        EnsureComputerUseAvailable();

        var result = await _browser.FillAsync(selector, value, cancellationToken);
        _computerUse.Touch("browser-input", $"select:{result.Role}:{result.Name}");
        await audit.WriteAsync("browser.select", Describe(selector), $"ok:{result.Role}:{Trim(result.Name)}", cancellationToken);
        return result;
    }

    public static string NormalizeWaitState(string? state)
        => (state ?? "visible").Trim().ToLowerInvariant() switch
        {
            "present" => "present",
            "visible" => "visible",
            "hidden" => "hidden",
            "absent" => "absent",
            _ => throw new ArgumentException("Browser wait state must be present, visible, hidden, or absent.", nameof(state))
        };

    public static string NormalizeSelectRole(string? role)
        => (role ?? "combobox").Trim().ToLowerInvariant() switch
        {
            "combobox" => "combobox",
            "listbox" => "listbox",
            _ => throw new ArgumentException("Browser select role must be combobox or listbox.", nameof(role))
        };

    public static IReadOnlyList<BrowserElementInfo> MatchElements(
        IEnumerable<BrowserElementInfo> elements,
        BrowserSelector selector)
        => Match(elements, selector).ToArray();

    private static IEnumerable<BrowserElementInfo> Match(IEnumerable<BrowserElementInfo> elements, BrowserSelector selector)
    {
        static bool Same(string? left, string? right)
            => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

        return elements.Where(element =>
            (string.IsNullOrWhiteSpace(selector.Role) || Same(element.Role, selector.Role)) &&
            (string.IsNullOrWhiteSpace(selector.Name) || Same(element.Name, selector.Name)) &&
            (string.IsNullOrWhiteSpace(selector.Text) || Same(element.Text, selector.Text)) &&
            (string.IsNullOrWhiteSpace(selector.Label) || Same(element.Label, selector.Label)) &&
            (string.IsNullOrWhiteSpace(selector.TestId) || Same(element.TestId, selector.TestId)));
    }

    private static BrowserElementInfo? Resolve(IReadOnlyList<BrowserElementInfo> matches, int? index)
    {
        if (index is not null)
            return index.Value >= 0 && index.Value < matches.Count ? matches[index.Value] : null;
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool IsSatisfied(string state, IReadOnlyList<BrowserElementInfo> matches, BrowserElementInfo? resolved, int? index)
        => state switch
        {
            "absent" => matches.Count == 0 || (index is not null && resolved is null),
            "present" => resolved is not null,
            "visible" => resolved?.Visible == true,
            "hidden" => resolved?.Visible == false,
            _ => false
        };

    private static void ValidateSemanticSelector(BrowserSelector selector)
    {
        if (string.IsNullOrWhiteSpace(selector.Role) &&
            string.IsNullOrWhiteSpace(selector.Name) &&
            string.IsNullOrWhiteSpace(selector.Text) &&
            string.IsNullOrWhiteSpace(selector.Label) &&
            string.IsNullOrWhiteSpace(selector.TestId))
            throw new ArgumentException("A semantic browser selector requires role, name, text, label, or testId.", nameof(selector));
        if (selector.Index is < 0)
            throw new ArgumentException("Browser selector index must be zero or greater.", nameof(selector));
    }

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
        if (!string.IsNullOrWhiteSpace(selector.Role)) parts.Add($"role={Trim(selector.Role)}");
        if (!string.IsNullOrWhiteSpace(selector.Name)) parts.Add($"name={Trim(selector.Name)}");
        if (!string.IsNullOrWhiteSpace(selector.Text)) parts.Add($"text={Trim(selector.Text)}");
        if (!string.IsNullOrWhiteSpace(selector.Label)) parts.Add($"label={Trim(selector.Label)}");
        if (!string.IsNullOrWhiteSpace(selector.TestId)) parts.Add($"testId={Trim(selector.TestId)}");
        if (selector.Index is not null) parts.Add($"index={selector.Index}");
        return string.Join(',', parts);
    }

    private static string Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = value.Trim();
        return value.Length <= 120 ? value : value[..120] + "…";
    }
}

public sealed record BrowserWaitResult(
    bool Ok,
    string State,
    long ElapsedMs,
    int MatchCount,
    string? Role,
    string? Name,
    bool? Visible,
    string? Url);
