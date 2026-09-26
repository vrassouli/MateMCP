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
        // Keep the initialization hook available for platform-specific WebView setup.
        // Mac Catalyst handles Tab traversal in the document capture handler below
        // because WKPreferences.tabFocusesLinks is not exposed to Catalyst.
    }
}
