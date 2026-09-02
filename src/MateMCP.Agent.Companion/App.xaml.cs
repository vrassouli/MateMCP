using MateMCP.Agent.Companion.Services;

namespace MateMCP.Agent.Companion;

public partial class App : Application
{
    private readonly ApprovalNotificationWatcher _approvalWatcher;

    public App(ApprovalNotificationWatcher approvalWatcher)
    {
        InitializeComponent();
        _approvalWatcher = approvalWatcher;
        _approvalWatcher.Start();
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new(new MainPage())
        {
            Title = "MateMCP Companion"
        };
}
