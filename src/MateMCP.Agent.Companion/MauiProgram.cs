using Bluent.UI.Extensions;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using MateMCP.Agent.Companion.Services;

#if MACCATALYST
using Microsoft.AspNetCore.Components.WebView.Maui;
#endif

namespace MateMCP.Agent.Companion;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddBluentUI();
        builder.Services.AddSingleton<IFolderPicker>(FolderPicker.Default);
        builder.Services.AddSingleton<AgentApiClient>();
        builder.Services.AddSingleton<AgentProcessController>();
        builder.Services.AddSingleton<DesktopUpdateService>();
        builder.Services.AddSingleton<AgentCompatibilityService>();
        builder.Services.AddSingleton<NativeApprovalNotifier>();
        builder.Services.AddSingleton<ApprovalNotificationWatcher>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

#if MACCATALYST
        // Keep a reference to the Companion window's WKWebView so MateMcpApplication's
        // application-wide Tab/Shift+Tab UIKeyCommands can forward keypresses into the
        // page's own focus-traversal helper when the WebView itself doesn't handle Tab.
        // See MateMcpNativeTabBridge and MateMcpApplication. Refs #277.
        BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
            "MateMcpNativeTabBridge",
            (handler, _) => MateMcpNativeTabBridge.ActiveWebView = handler.PlatformView);
#endif

        return builder.Build();
    }
}
