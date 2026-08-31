using System.Diagnostics;
using MateMCP.Agent.Security;

namespace MateMCP.Agent.Desktop;

public sealed class LocalNotificationService(ILogger<LocalNotificationService> logger)
{
    public async Task NotifyApprovalAsync(int port, PendingApproval approval, CancellationToken ct)
    {
        try
        {
            var title = "MateMCP approval required";
            var message = $"{approval.Capability}: {approval.Target}";
            var managementUrl = $"http://127.0.0.1:{port}/ui#approvals";

            if (OperatingSystem.IsMacOS())
            {
                using var notification = NewProcess("/usr/bin/osascript", "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, message);
                notification.Start();
                await notification.WaitForExitAsync(ct);

                using var browser = NewProcess("/usr/bin/open", managementUrl);
                browser.Start();
                await browser.WaitForExitAsync(ct);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                await TryShowWindowsToastAsync(title, message, ct);
                Process.Start(new ProcessStartInfo(managementUrl) { UseShellExecute = true });
            }
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
            logger.LogDebug(ex, "Could not show Windows toast notification.");
        }
    }

    private static string EscapePowerShell(string value)
        => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static Process NewProcess(string fileName, params string[] args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return new Process { StartInfo = start };
    }
}
