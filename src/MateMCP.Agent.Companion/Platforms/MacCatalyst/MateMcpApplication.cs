using Foundation;
using ObjCRuntime;
using UIKit;

namespace MateMCP.Agent.Companion;

/// <summary>
/// Registers application-wide UIKeyCommands for Tab and Shift+Tab so keyboard focus
/// navigation in the Companion UI works the same way it does in native Mac apps, even
/// though that UI is hosted inside a WKWebView (see MateMcpNativeTabBridge for why the
/// WebView alone can't be relied on to handle Tab itself).
///
/// The commands explicitly request priority over UIKit's normal system focus movement.
/// This is important for Tab on Mac Catalyst: without priority, WebKit/UIKit can consume
/// the navigation key before our application-level command gets a useful chance to act.
/// The command action bypasses WebKit's native Tab traversal entirely and calls the DOM
/// focus helper through MateMcpNativeTabBridge.
///
/// Refs #277.
/// </summary>
[Register("MateMcpApplication")]
public class MateMcpApplication : UIApplication
{
    public override UIKeyCommand[] KeyCommands =>
    [
        MateMcpNativeTabBridge.CreatePriorityCommand((UIKeyModifierFlags)0, "mateMcpTabForward:"),
        MateMcpNativeTabBridge.CreatePriorityCommand(UIKeyModifierFlags.Shift, "mateMcpTabBackward:")
    ];

    [Export("mateMcpTabForward:")]
    private void MateMcpTabForward(UIKeyCommand command) => MateMcpNativeTabBridge.AdvanceFocus(shiftKey: false);

    [Export("mateMcpTabBackward:")]
    private void MateMcpTabBackward(UIKeyCommand command) => MateMcpNativeTabBridge.AdvanceFocus(shiftKey: true);
}
