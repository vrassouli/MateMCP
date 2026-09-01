using Bluent.UI.Extensions;
using MateMCP.Agent.Companion.Services;

namespace MateMCP.Agent.Companion;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddBluentUI();
        builder.Services.AddSingleton<AgentApiClient>();
        builder.Services.AddSingleton<NativeApprovalNotifier>();
        builder.Services.AddSingleton<ApprovalNotificationWatcher>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        return builder.Build();
    }
}
