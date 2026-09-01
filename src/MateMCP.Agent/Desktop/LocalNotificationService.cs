using MateMCP.Agent.Security;

namespace MateMCP.Agent.Desktop;

/// <summary>
/// Passive fallback notification owned by the background Agent.
/// The Companion application provides actionable Approve/Deny buttons. This service intentionally
/// never launches a browser and stays silent while the Companion process is running.
/// </summary>
public sealed class LocalNotificationService(ILogger<LocalNotificationService> logger)
{
    public async Task NotifyApprovalAsync(int port, PendingApproval approval, CancellationToken ct)
    {
        try
        {
            if (IsCompanionRunning())
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

    private static bool IsCompanionRunning()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("MateMCP.Agent.Companion");
            try { return processes.Length > 0; }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        catch
        {
            return false;
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
