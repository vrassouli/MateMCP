using System.Security.Cryptography;

namespace MateMCP.Relay;

public sealed class LogicalSessionRegistry
{
    private const int MaxSessionIdLength = 128;
    private readonly object _gate = new();
    private readonly Dictionary<string, LogicalSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _retention;
    private readonly int _maxEntries;
    private readonly Func<DateTimeOffset> _utcNow;

    public LogicalSessionRegistry()
        : this(2048, TimeSpan.FromHours(12), () => DateTimeOffset.UtcNow) { }

    internal LogicalSessionRegistry(int maxEntries, TimeSpan retention, Func<DateTimeOffset> utcNow)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        _maxEntries = maxEntries;
        _retention = retention;
        _utcNow = utcNow;
    }

    public LogicalSessionResolution Resolve(string? requestedSessionId, string userId, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User id is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Device id is required.", nameof(deviceId));

        var now = _utcNow();
        lock (_gate)
        {
            PruneLocked(now);

            if (!string.IsNullOrWhiteSpace(requestedSessionId))
            {
                var normalized = Normalize(requestedSessionId);
                if (_sessions.TryGetValue(normalized, out var existing))
                {
                    if (!string.Equals(existing.UserId, userId, StringComparison.Ordinal) ||
                        !string.Equals(existing.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                        return LogicalSessionResolution.Conflict(normalized);

                    existing.LastSeenAt = now;
                    return LogicalSessionResolution.Existing(normalized);
                }

                EnsureCapacityLocked();
                _sessions.Add(normalized, new LogicalSession(normalized, userId, deviceId, now));
                return LogicalSessionResolution.Accepted(normalized);
            }

            EnsureCapacityLocked();
            string generated;
            do generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            while (_sessions.ContainsKey(generated));
            _sessions.Add(generated, new LogicalSession(generated, userId, deviceId, now));
            return LogicalSessionResolution.Generated(generated);
        }
    }

    internal int Count
    {
        get { lock (_gate) return _sessions.Count; }
    }

    private static string Normalize(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is 0 or > MaxSessionIdLength)
            throw new InvalidLogicalSessionIdException();
        if (normalized.Any(ch => ch < 0x21 || ch > 0x7e))
            throw new InvalidLogicalSessionIdException();
        return normalized;
    }

    private void PruneLocked(DateTimeOffset now)
    {
        foreach (var id in _sessions.Values
                     .Where(x => now - x.LastSeenAt >= _retention)
                     .Select(x => x.SessionId)
                     .ToArray())
            _sessions.Remove(id);
    }

    private void EnsureCapacityLocked()
    {
        while (_sessions.Count >= _maxEntries)
        {
            var oldest = _sessions.Values.OrderBy(x => x.LastSeenAt).First();
            _sessions.Remove(oldest.SessionId);
        }
    }

    private sealed class LogicalSession(string sessionId, string userId, string deviceId, DateTimeOffset now)
    {
        public string SessionId { get; } = sessionId;
        public string UserId { get; } = userId;
        public string DeviceId { get; } = deviceId;
        public DateTimeOffset LastSeenAt { get; set; } = now;
    }
}

public sealed record LogicalSessionResolution(string SessionId, bool IsGenerated, bool IsConflict, bool IsNew)
{
    public static LogicalSessionResolution Generated(string id) => new(id, true, false, true);
    public static LogicalSessionResolution Accepted(string id) => new(id, false, false, true);
    public static LogicalSessionResolution Existing(string id) => new(id, false, false, false);
    public static LogicalSessionResolution Conflict(string id) => new(id, false, true, false);
}

public sealed class InvalidLogicalSessionIdException()
    : ArgumentException("Mcp-Session-Id must contain 1..128 visible ASCII characters.");
