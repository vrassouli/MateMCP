namespace MateMCP.Agent.Desktop;

/// <summary>
/// Tracks unprotected UI controls that currently contain a locally injected secret.
/// The guard stores target identity only, never the secret value. While such a value
/// remains visible, pixel/DOM capture is suppressed and semantic snapshots redact it.
/// </summary>
public sealed class SensitiveUiGuard
{
    public static SensitiveUiGuard Shared { get; } = new();

    private readonly object _sync = new();
    private readonly Dictionary<string, HashSet<string>> _elements = new(StringComparer.OrdinalIgnoreCase);

    public bool Active
    {
        get
        {
            lock (_sync) return _elements.Values.Any(items => items.Count > 0);
        }
    }

    public static string CaptureBlockedMessage { get; } =
        "Visual/DOM capture is temporarily blocked because an unprotected UI field contains a MateMCP-injected secret. " +
        "Use semantic UI actions without visual capture, overwrite/clear the field, or submit/navigate away and then take a new ui_snapshot so MateMCP can verify the secret is no longer visible.";

    public void Mark(string windowId, string elementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        lock (_sync)
        {
            if (!_elements.TryGetValue(windowId, out var items))
            {
                items = new HashSet<string>(StringComparer.Ordinal);
                _elements[windowId] = items;
            }
            items.Add(elementId);
        }
    }

    public void Clear(string windowId, string elementId)
    {
        if (string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(elementId)) return;
        lock (_sync)
        {
            if (!_elements.TryGetValue(windowId, out var items)) return;
            items.Remove(elementId);
            if (items.Count == 0) _elements.Remove(windowId);
        }
    }

    public UiSnapshot RedactAndReconcile(UiSnapshot snapshot)
    {
        lock (_sync)
        {
            if (!_elements.TryGetValue(snapshot.WindowId, out var items) || items.Count == 0) return snapshot;

            var byId = snapshot.Elements.ToDictionary(element => element.Id, StringComparer.Ordinal);
            foreach (var elementId in items.ToArray())
            {
                if (byId.TryGetValue(elementId, out var element))
                {
                    if (string.IsNullOrEmpty(element.Value)) items.Remove(elementId);
                }
                else if (!snapshot.Truncated)
                {
                    items.Remove(elementId);
                }
            }

            if (items.Count == 0)
            {
                _elements.Remove(snapshot.WindowId);
                return snapshot;
            }

            var redacted = snapshot.Elements
                .Select(element => items.Contains(element.Id) ? element with { Value = null } : element)
                .ToArray();
            return snapshot with { Elements = redacted };
        }
    }

    internal void ResetForTests()
    {
        lock (_sync) _elements.Clear();
    }
}
