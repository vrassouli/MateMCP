using WebKit;

namespace MateMCP.Agent.Companion;

/// <summary>
/// WKWebView on Mac Catalyst does not reliably route the physical Tab key into the DOM as
/// a keydown -- UIKit's own responder chain can consume it before web content ever sees it,
/// particularly before any element inside the page already holds WebKit-level focus. This
/// bridge is called from <see cref="MateMcpApplication"/>'s application-wide priority
/// UIKeyCommands and forwards Tab/Shift+Tab into the page's
/// own focus-traversal helper (see wwwroot/index.html, window.mateMcpFocus.advance).
///
/// Refs #277.
/// </summary>
internal static class MateMcpNativeTabBridge
{
    /// <summary>
    /// Set from the BlazorWebViewHandler.Mapper customization in MauiProgram.cs once the
    /// Companion window's WKWebView has been created.
    /// </summary>
    public static WKWebView? ActiveWebView { get; set; }

    public static void AdvanceFocus(bool shiftKey)
    {
        var webView = ActiveWebView;
        if (webView is null)
        {
            return;
        }

        var script = $"window.mateMcpFocus && window.mateMcpFocus.advance({(shiftKey ? "true" : "false")});";
        webView.EvaluateJavaScript(script, (_, _) => { });
    }
}
