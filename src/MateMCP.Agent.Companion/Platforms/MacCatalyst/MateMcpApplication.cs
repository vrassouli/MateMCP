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
    // On macOS/Catalyst, Shift+Tab can surface as the legacy back-tab control
    // character instead of a tab character plus a Shift modifier.
    private const string TabCharacter = "\t";
    private const string BackTabCharacter = "\u0019";

    public override UIKeyCommand[] KeyCommands =>
    [
        MateMcpNativeTabBridge.CreatePriorityCommand((UIKeyModifierFlags)0, "mateMcpTabForward:"),
        MateMcpNativeTabBridge.CreatePriorityCommand(UIKeyModifierFlags.Shift, "mateMcpTabBackward:")
    ];


    public override void SendEvent(UIEvent uievent)
    {
        if (uievent is UIPressesEvent pressesEvent)
        {
            if (TryHandleTabPress(pressesEvent))
            {
                return;
            }

            ForwardEscapeToDom(pressesEvent);
        }

        base.SendEvent(uievent);
    }

    private static bool TryHandleTabPress(UIPressesEvent pressesEvent)
    {
        foreach (var press in pressesEvent.AllPresses)
        {
            var key = press.Key;
            if (key is null)
            {
                continue;
            }

            var characters = key.CharactersIgnoringModifiers;
            if (characters is not TabCharacter and not BackTabCharacter)
            {
                continue;
            }

            // Preserve real shortcuts such as Command+Tab, Control+Tab, and Option+Tab.
            // Shift is the only modifier that changes MateMCP's focus traversal direction.
            const UIKeyModifierFlags shortcutModifiers =
                UIKeyModifierFlags.Command | UIKeyModifierFlags.Control | UIKeyModifierFlags.Alternate;
            if ((key.ModifierFlags & shortcutModifiers) != 0)
            {
                continue;
            }

            if (press.Phase == UIPressPhase.Began)
            {
                MateMcpNativeTabBridge.AdvanceFocus(
                    shiftKey: characters == BackTabCharacter ||
                              (key.ModifierFlags & UIKeyModifierFlags.Shift) != 0);
            }

            // Swallow every phase of the matching Tab press. If UIKit/WebKit receives the same
            // event after our DOM traversal, its system focus movement can consume or duplicate
            // the navigation. SendEvent is intentionally the single pre-dispatch owner for Tab.
            return true;
        }

        return false;
    }

    private static void ForwardEscapeToDom(UIPressesEvent pressesEvent)
    {
        foreach (var press in pressesEvent.AllPresses)
        {
            var key = press.Key;
            if (key is null || key.KeyCode != UIKeyboardHidUsage.KeyboardEscape)
            {
                continue;
            }

            const UIKeyModifierFlags shortcutModifiers =
                UIKeyModifierFlags.Command | UIKeyModifierFlags.Control | UIKeyModifierFlags.Alternate;
            if ((key.ModifierFlags & shortcutModifiers) != 0)
            {
                continue;
            }

            if (press.Phase == UIPressPhase.Began)
            {
                // Do not swallow Escape. WebKit/UIKit still receives the original event; this
                // DOM call is only a fallback for Catalyst builds where HTML dialog cancellation
                // is lost before it reaches the page. If no dialog is open, it is a no-op.
                MateMcpNativeTabBridge.CloseTopDialog();
            }

            return;
        }
    }

    [Export("mateMcpTabForward:")]
    private void MateMcpTabForward(UIKeyCommand command) => MateMcpNativeTabBridge.AdvanceFocus(shiftKey: false);

    [Export("mateMcpTabBackward:")]
    private void MateMcpTabBackward(UIKeyCommand command) => MateMcpNativeTabBridge.AdvanceFocus(shiftKey: true);
}
