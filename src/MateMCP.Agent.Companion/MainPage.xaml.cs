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
        // WebKit exposes tabFocusesLinks to the Catalyst/iOS runtime starting in 26,
        // but the current .NET Catalyst binding does not expose the managed property.
        // Set it through KVC before WKWebView creation so plain Tab participates in
        // normal form-control focus traversal. Do not gate this on a selector capability check:
        // that check can report false for this WebKit property on Catalyst.
        if (OperatingSystem.IsMacCatalystVersionAtLeast(26))
        {
            using var key = new Foundation.NSString("tabFocusesLinks");
            using var value = Foundation.NSNumber.FromBoolean(true);
            e.Configuration.Preferences.SetValueForKey(value, key);
        }
#endif
    }
}
