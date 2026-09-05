namespace MateMCP.Relay;

public sealed class RelayInstanceIdentity
{
    public RelayInstanceIdentity()
    {
        StartedAt = DateTimeOffset.UtcNow;
        var configured = Environment.GetEnvironmentVariable("MATEMCP_RELAY_INSTANCE_ID");
        var fallback = $"relay-{Environment.ProcessId}-{Guid.NewGuid():N}";
        InstanceId = Sanitize(string.IsNullOrWhiteSpace(configured) ? fallback : configured);
    }

    public string InstanceId { get; }
    public DateTimeOffset StartedAt { get; }

    private static string Sanitize(string value)
    {
        var safe = new string(value.Trim().Take(64).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "relay" : safe;
    }
}
