using Foundation;
using ObjCRuntime;
using UIKit;
using WebKit;

namespace MateMCP.Agent.Companion;

/// <summary>
/// Routes Tab/Shift+Tab around Mac Catalyst + WKWebView focus handling and into the
/// page's deterministic DOM traversal helper. Priority commands are installed on the
/// UIViewController that owns the WKWebView (close enough in the responder chain to own
/// system focus movement) and, through <see cref="MateMcpApplication"/>, at application
/// scope as a fallback for the no-WebView-first-responder case.
///
/// Refs #277/#231.
/// </summary>
internal static class MateMcpNativeTabBridge
{
    private static WKWebView? _activeWebView;
    private static UIViewController? _activeController;
    private static UIKeyCommand? _controllerForwardCommand;
    private static UIKeyCommand? _controllerBackwardCommand;

    public static WKWebView? ActiveWebView => _activeWebView;

    /// <summary>
    /// Called by the BlazorWebView handler mapping once the native WKWebView exists.
    /// Application-level commands are reached too late when a focused WebKit control
    /// consumes Tab as system focus movement, so the same priority commands are installed
    /// on the nearest owning UIViewController and dispatch directly to the JS focus helper.
    /// </summary>
    public static void Attach(WKWebView webView)
    {
        if (ReferenceEquals(_activeWebView, webView))
        {
            return;
        }

        DetachCurrentController();
        _activeWebView = webView;

        UIResponder? current = webView.NextResponder;
        while (current is not null and not UIViewController)
        {
            current = current.NextResponder;
        }

        if (current is not UIViewController controller)
        {
            return;
        }

        _activeController = controller;
        _controllerForwardCommand = CreatePriorityCommand((UIKeyModifierFlags)0, "mateMcpTabForward:");
        _controllerBackwardCommand = CreatePriorityCommand(UIKeyModifierFlags.Shift, "mateMcpTabBackward:");
        controller.AddKeyCommand(_controllerForwardCommand);
        controller.AddKeyCommand(_controllerBackwardCommand);
    }

    internal static UIKeyCommand CreatePriorityCommand(UIKeyModifierFlags modifiers, string selector)
    {
        var command = UIKeyCommand.Create(new NSString("\t"), modifiers, new Selector(selector));
        command.WantsPriorityOverSystemBehavior = true;
        return command;
    }

    public static void AdvanceFocus(bool shiftKey)
    {
        var webView = _activeWebView;
        if (webView is null)
        {
            return;
        }

        var script = $"window.mateMcpFocus && window.mateMcpFocus.advance({(shiftKey ? "true" : "false")});";
        webView.EvaluateJavaScript(script, (_, _) => { });
    }

    private static void DetachCurrentController()
    {
        if (_activeController is not null)
        {
            if (_controllerForwardCommand is not null)
            {
                _activeController.RemoveKeyCommand(_controllerForwardCommand);
            }

            if (_controllerBackwardCommand is not null)
            {
                _activeController.RemoveKeyCommand(_controllerBackwardCommand);
            }
        }

        _controllerForwardCommand?.Dispose();
        _controllerBackwardCommand?.Dispose();
        _controllerForwardCommand = null;
        _controllerBackwardCommand = null;
        _activeController = null;
        _activeWebView = null;
    }
}
