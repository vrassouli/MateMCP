#if WINDOWS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
#elif MACCATALYST
using Foundation;
using UserNotifications;
#endif

namespace MateMCP.Agent.Companion.Services;

public sealed class NativeApprovalNotifier(AgentApiClient api) : IDisposable
{
    private bool _initialized;
    public bool IsAvailable { get; private set; }

#if WINDOWS
    private AppNotificationManager? _windowsManager;
#elif MACCATALYST
    private MacNotificationDelegate? _macDelegate;
#endif

    public async Task InitializeAsync()
    {
        if (_initialized) return;

#if WINDOWS
        // AppNotificationManager depends on the Windows App SDK Singleton package.
        // Self-contained desktop deployments can legitimately report it unsupported.
        if (!AppNotificationManager.IsSupported())
        {
            _initialized = true;
            IsAvailable = false;
            return;
        }

        _windowsManager = AppNotificationManager.Default;
        _windowsManager.NotificationInvoked += OnWindowsNotificationInvoked;
        _windowsManager.Register();
        IsAvailable = _windowsManager.Setting == AppNotificationSetting.Enabled;
#elif MACCATALYST
        var center = UNUserNotificationCenter.Current;
        _macDelegate = new MacNotificationDelegate(DecideFromNotificationAsync);
        center.Delegate = _macDelegate;

        var actions = new[]
        {
            UNNotificationAction.FromIdentifier("allow", "Approve", UNNotificationActionOptions.None),
            UNNotificationAction.FromIdentifier("allow-session", "Approve for session", UNNotificationActionOptions.None),
            UNNotificationAction.FromIdentifier("allow-always", "Always allow", UNNotificationActionOptions.None),
            UNNotificationAction.FromIdentifier("deny", "Deny", UNNotificationActionOptions.Destructive)
        };
        var category = UNNotificationCategory.FromIdentifier("matemcp.approval", actions, [], UNNotificationCategoryOptions.None);
        center.SetNotificationCategories(new NSSet<UNNotificationCategory>(category));
        await center.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound);
        IsAvailable = true;
#else
        await Task.CompletedTask;
        IsAvailable = false;
#endif

        _initialized = true;
    }

    public async Task ShowAsync(PendingApproval approval)
    {
        await InitializeAsync();
        if (!IsAvailable) return;

#if WINDOWS
        var notification = new AppNotificationBuilder()
            .AddArgument("approvalId", approval.Id)
            .AddText("MateMCP approval required")
            .AddText($"{approval.Capability}: {approval.Target}")
            .AddText(approval.Summary)
            .AddButton(new AppNotificationButton("Approve")
                .AddArgument("approvalId", approval.Id)
                .AddArgument("decision", "allow"))
            .AddButton(new AppNotificationButton("Approve for session")
                .AddArgument("approvalId", approval.Id)
                .AddArgument("decision", "allow-session"))
            .AddButton(new AppNotificationButton("Always allow")
                .AddArgument("approvalId", approval.Id)
                .AddArgument("decision", "allow-always"))
            .AddButton(new AppNotificationButton("Deny")
                .AddArgument("approvalId", approval.Id)
                .AddArgument("decision", "deny"))
            .BuildNotification();
        _windowsManager!.Show(notification);
#elif MACCATALYST
        var content = new UNMutableNotificationContent
        {
            Title = "MateMCP approval required",
            Subtitle = $"{approval.Capability}: {approval.Target}",
            Body = approval.Summary,
            CategoryIdentifier = "matemcp.approval",
            Sound = UNNotificationSound.Default
        };
        var request = UNNotificationRequest.FromIdentifier(approval.Id, content, null);
        await UNUserNotificationCenter.Current.AddNotificationRequestAsync(request);
#else
        await Task.CompletedTask;
#endif
    }

    private async Task DecideFromNotificationAsync(string approvalId, string decision)
    {
        if (string.IsNullOrWhiteSpace(approvalId) || decision is not ("allow" or "allow-session" or "allow-always" or "deny")) return;
        try { await api.DecideApprovalAsync(approvalId, decision); }
        catch { /* Request may have expired or been handled from the Companion UI. */ }
    }

#if WINDOWS
    private async void OnWindowsNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("approvalId", out var approvalId) ||
            !args.Arguments.TryGetValue("decision", out var decision)) return;
        await DecideFromNotificationAsync(approvalId, decision);
    }
#elif MACCATALYST
    private sealed class MacNotificationDelegate(Func<string, string, Task> decide) : UNUserNotificationCenterDelegate
    {
        public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
        {
            var approvalId = response.Notification.Request.Identifier.ToString();
            var decision = response.ActionIdentifier.ToString();
            if (decision is "allow" or "allow-session" or "allow-always" or "deny")
                _ = decide(approvalId, decision);
            completionHandler();
        }

        public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification,
            Action<UNNotificationPresentationOptions> completionHandler)
            => completionHandler(UNNotificationPresentationOptions.List | UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound);
    }
#endif

    public void Dispose()
    {
#if WINDOWS
        if (_windowsManager is not null)
        {
            _windowsManager.NotificationInvoked -= OnWindowsNotificationInvoked;
            try { _windowsManager.Unregister(); } catch { }
        }
#endif
    }
}
