using System.Collections.Concurrent;
using MateMCP.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Security;

public sealed class CredentialInjectionRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _limit;
    private readonly TimeSpan _window;

    public CredentialInjectionRateLimiter(IOptions<MateOptions> options)
    {
        var settings = options.Value.InteractiveShell ?? new InteractiveShellOptions();
        _limit = Math.Clamp(settings.SecretInjectionMaxAttempts, 1, 100);
        _window = TimeSpan.FromSeconds(Math.Clamp(settings.SecretInjectionWindowSeconds, 1, 3600));
    }

    public bool TryAcquire(string credential, out TimeSpan retryAfter)
    {
        var now = DateTimeOffset.UtcNow;
        var attempts = _attempts.GetOrAdd(credential, _ => new Queue<DateTimeOffset>());
        lock (attempts)
        {
            while (attempts.TryPeek(out var oldest) && now - oldest >= _window) attempts.Dequeue();
            if (attempts.Count >= _limit)
            {
                retryAfter = _window - (now - attempts.Peek());
                return false;
            }

            attempts.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}
