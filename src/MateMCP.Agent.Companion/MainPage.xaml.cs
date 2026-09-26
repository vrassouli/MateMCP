namespace MateMCP.Agent.Companion;

public partial class MainPage : ContentPage
{
#if MACCATALYST
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendBooleanProperty(
        IntPtr receiver,
        IntPtr selector,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)] bool value);
#endif

    public MainPage()
    {
        InitializeComponent();
    }

    private static void OnBlazorWebViewInitializing(
        object? sender,
        Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializingEventArgs e)
    {
#if MACCATALYST
        // WebKit exposes setTabFocusesLinks: on the Catalyst/iOS 26+ runtime,
        // but the current .NET Catalyst binding does not expose the managed property.
        // Invoke the Objective-C setter directly before WKWebView creation so plain
        // Tab participates in normal form-control focus traversal.
        if (OperatingSystem.IsMacCatalystVersionAtLeast(26))
        {
            var selector = ObjCRuntime.Selector.GetHandle("setTabFocusesLinks:");
            SendBooleanProperty(e.Configuration.Preferences.Handle, selector, true);
        }
#endif
    }

    private static void OnBlazorWebViewInitialized(
        object? sender,
        Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
    {
#if MACCATALYST
        // Re-apply the preference to the actual WKWebView instance. This catches
        // any configuration replacement/copying that happens during WebView creation.
        if (OperatingSystem.IsMacCatalystVersionAtLeast(26))
        {
            var selector = ObjCRuntime.Selector.GetHandle("setTabFocusesLinks:");
            SendBooleanProperty(e.WebView.Configuration.Preferences.Handle, selector, true);
        }
#endif
    }
}
