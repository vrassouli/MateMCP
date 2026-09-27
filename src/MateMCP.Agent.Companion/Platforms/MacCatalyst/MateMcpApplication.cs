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
        CreateTabCommand((UIKeyModifierFlags)0, "mateMcpTabForward:"),
        CreateTabCommand(UIKeyModifierFlags.Shift, "mateMcpTabBackward:")
    ];

    private static UIKeyCommand CreateTabCommand(UIKeyModifierFlags modifiers, string selector)
    {
        var command = UIKeyCommand.Create(new NSString("\t"), modifiers, new Selector(selector));
        // Tab is a system focus-navigation key on Mac Catalyst. Without this flag, UIKit/WebKit
        // can consume the focus movement before our application-level command is invoked. Previous
        // experiments used priority commands but routed them back into insertTab:/insertBacktab:,
        // which still depended on the broken WebKit traversal path. This command instead owns the
        // Tab keystroke and dispatches directly to MateMcpNativeTabBridge. Refs #277/#231.
        command.WantsPriorityOverSystemBehavior = true;
        return command;
    }

    [Export("mateMcpTabForward:")]
    private void MateMcpTabForward(UIKeyCommand command) => MateMcpNativeTabBridge.AdvanceFocus(shiftKey: false);

    [Export("mateMcpTabBackward:")]
    private void MateMcpTabBackward(UIKeyCommand command) => MateMcpNativeTabBridge.AdvanceFocus(shiftKey: true);
}
