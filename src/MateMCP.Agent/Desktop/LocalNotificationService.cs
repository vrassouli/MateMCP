using System.Diagnostics;
using MateMCP.Agent.Security;

namespace MateMCP.Agent.Desktop;

public sealed class LocalNotificationService(ILogger<LocalNotificationService> logger)
{
    public async Task NotifyApprovalAsync(int port, PendingApproval approval, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return;

        try
        {
            var title = "MateMCP approval required";
            var message = $"{approval.Capability}: {approval.Target}";
            using var notification = NewProcess("/usr/bin/osascript", "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, message);
            notification.Start();
            await notification.WaitForExitAsync(ct);

            using var browser = NewProcess("/usr/bin/open", $"http://127.0.0.1:{port}/ui#approvals");
            browser.Start();
            await browser.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not show local approval notification.");
        }
    }

    private static Process NewProcess(string fileName, params string[] args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return new Process { StartInfo = start };
    }
}
