using Bluent.UI.Extensions;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using MateMCP.Agent.Companion.Services;

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

        return builder.Build();
    }
}
