using MateMCP.Agent.Companion.Services;

namespace MateMCP.Agent.Companion;

public partial class App : Application
{
    private readonly ApprovalNotificationWatcher _approvalWatcher;
    private readonly CompanionLifecycleStore _lifecycle;

    public App(
        ApprovalNotificationWatcher approvalWatcher,
        CompanionLifecycleStore lifecycle)
    {
        _approvalWatcher = approvalWatcher;
        _lifecycle = lifecycle;

        _lifecycle.BeginSession();
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        InitializeComponent();
        _approvalWatcher.Start();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage())
        {
            Title = "MateMCP Companion"
        };

        window.Destroying += (_, _) => _lifecycle.MarkTerminal("window-closed");
        return window;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        => _lifecycle.RecordUnhandled(args.ExceptionObject as Exception, args.IsTerminating);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
        => _lifecycle.RecordUnhandled(args.Exception, terminating: false);

    private void OnProcessExit(object? sender, EventArgs args)
        => _lifecycle.MarkTerminal("process-exit");
}
