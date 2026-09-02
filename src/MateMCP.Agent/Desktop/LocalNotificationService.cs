using MateMCP.Agent.Security;

namespace MateMCP.Agent.Desktop;

/// <summary>
/// Passive approval notification owned by the background Agent.
/// The Companion can add richer actionable notifications when the platform/runtime supports them,
/// but the Agent fallback must remain available so an approval is never silent.
/// </summary>
public sealed class LocalNotificationService(
    ILogger<LocalNotificationService> logger,
    CompanionNotificationPresence companionNotifications)
{
    public async Task NotifyApprovalAsync(int port, PendingApproval approval, CancellationToken ct)
    {
        try
        {
            // On macOS suppress the Agent fallback only after the Companion has explicitly
            // confirmed that its native notifier is initialized and available. Process
            // presence alone is not enough: during startup it created a race where the first
            // approval could arrive before the Companion notification watcher was ready.
            if (OperatingSystem.IsMacOS() && companionNotifications.IsReady)
                return;

            var title = "MateMCP approval required";
            var message = $"{approval.Capability}: {approval.Target}. Open MateMCP Agent Companion to review.";

            if (OperatingSystem.IsMacOS())
            {
                using var notification = NewProcess("/usr/bin/osascript", "-e", "on run argv", "-e",
                    "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, message);
                notification.Start();
                await notification.WaitForExitAsync(ct);
                return;
            }

            if (OperatingSystem.IsWindows())
                await TryShowWindowsToastAsync(title, message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not show local approval notification.");
        }
    }

    private async Task TryShowWindowsToastAsync(string title, string message, CancellationToken ct)
    {
        try
        {
            var escapedTitle = EscapePowerShell(title);
            var escapedMessage = EscapePowerShell(message);
            var script = $"""
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
[Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml('<toast><visual><binding template="ToastGeneric"><text>{escapedTitle}</text><text>{escapedMessage}</text></binding></visual></toast>')
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('MateMCP.Agent').Show($toast)
""";
            using var process = NewProcess("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script);
            process.Start();
            await process.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not show Windows fallback toast notification.");
        }
    }

    private static string EscapePowerShell(string value)
        => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static System.Diagnostics.Process NewProcess(string fileName, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return new System.Diagnostics.Process { StartInfo = start };
    }
}
