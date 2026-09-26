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
        // The current .NET Mac Catalyst binding does not expose WKPreferences.tabFocusesLinks,
        // so opt in through the documented Objective-C selector when the runtime provides it.
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
