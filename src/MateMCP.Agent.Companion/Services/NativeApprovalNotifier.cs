#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
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
    private bool _useCompatToasts;
    private bool _compatActivationRegistered;
#elif MACCATALYST
    private MacNotificationDelegate? _macDelegate;
#endif

    public async Task InitializeAsync()
    {
        if (_initialized) return;

#if WINDOWS
        // Prefer the Windows App SDK notification path when its runtime support is
        // actually present. Self-contained, unpackaged Desktop builds can lack the
        // Windows App SDK Singleton package, so IsSupported() legitimately returns
        // false on real installations even though the notification code compiles.
        if (AppNotificationManager.IsSupported())
        {
            try
            {
                _windowsManager = AppNotificationManager.Default;
                _windowsManager.NotificationInvoked += OnWindowsNotificationInvoked;
                _windowsManager.Register();
                IsAvailable = _windowsManager.Setting == AppNotificationSetting.Enabled;
                _initialized = true;
                return;
            }
            catch
            {
                if (_windowsManager is not null)
                {
                    _windowsManager.NotificationInvoked -= OnWindowsNotificationInvoked;
                    try { _windowsManager.Unregister(); } catch { }
                    _windowsManager = null;
                }
            }
        }

        // Microsoft.Toolkit.Uwp.Notifications provides native Windows toast support
        // for unpackaged Win32/.NET desktop applications and does not require the
        // Windows App SDK Singleton package. This keeps approval actions available
        // in the self-contained MateMCP Desktop installation used in the field.
        ToastNotificationManagerCompat.OnActivated += OnCompatNotificationActivated;
        _compatActivationRegistered = true;
        _useCompatToasts = true;
        IsAvailable = true;
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
        var authorization = await center.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound);
        IsAvailable = authorization.Item1;
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
        if (_useCompatToasts)
        {
            new ToastContentBuilder()
                .AddText("MateMCP approval required")
                .AddText($"{approval.Capability}: {approval.Target}")
                .AddText(approval.Summary)
                .AddButton(new ToastButton()
                    .SetContent("Approve")
                    .AddArgument("approvalId", approval.Id)
                    .AddArgument("decision", "allow"))
                .AddButton(new ToastButton()
                    .SetContent("Approve for session")
                    .AddArgument("approvalId", approval.Id)
                    .AddArgument("decision", "allow-session"))
                .AddButton(new ToastButton()
                    .SetContent("Always allow")
                    .AddArgument("approvalId", approval.Id)
                    .AddArgument("decision", "allow-always"))
                .AddButton(new ToastButton()
                    .SetContent("Deny")
                    .AddArgument("approvalId", approval.Id)
                    .AddArgument("decision", "deny"))
                .Show();
            return;
        }

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

    private async void OnCompatNotificationActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        var parsed = ToastArguments.Parse(args.Argument);
        if (!parsed.TryGetValue("approvalId", out var approvalId) ||
            !parsed.TryGetValue("decision", out var decision)) return;
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
        if (_compatActivationRegistered)
            ToastNotificationManagerCompat.OnActivated -= OnCompatNotificationActivated;
#endif
    }
}
