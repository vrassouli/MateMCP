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
        // Install priority Tab/Shift+Tab commands on the WKWebView owning controller and keep
        // the application-level commands as a fallback. Both paths dispatch into the same DOM
        // focus helper. See MateMcpNativeTabBridge and MateMcpApplication. Refs #277/#231.
        BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
            "MateMcpNativeTabBridge",
            (handler, _) => MateMcpNativeTabBridge.Attach(handler.PlatformView));
#endif

        return builder.Build();
    }
}
