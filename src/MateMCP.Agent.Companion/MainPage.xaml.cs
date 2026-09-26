namespace MateMCP.Agent.Companion;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private static void OnBlazorWebViewInitializing(
        object? sender,
        Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializingEventArgs e)
    {
#if MACCATALYST
        // WKWebView otherwise keeps Tab inside the current text control on Mac Catalyst.
        // WebKit exposes tabFocusesLinks to Catalyst starting with the 26.x runtime. Use the
        // strongly-typed binding there: the older KVC/RespondsToSelector path can report that
        // the selector is unavailable even though Option+Tab proves WebKit traversal works.
        if (OperatingSystem.IsMacCatalystVersionAtLeast(26))
        {
#pragma warning disable CA1416
            e.Configuration.Preferences.TabFocusesLinks = true;
#pragma warning restore CA1416
            return;
        }

        // Keep the best-effort selector fallback for older Catalyst runtimes.
        var setTabFocusesLinks = new ObjCRuntime.Selector("setTabFocusesLinks:");
        if (e.Configuration.Preferences.RespondsToSelector(setTabFocusesLinks))
        {
            using var key = new Foundation.NSString("tabFocusesLinks");
            using var value = Foundation.NSNumber.FromBoolean(true);
            e.Configuration.Preferences.SetValueForKey(value, key);
        }
#endif
    }
}
