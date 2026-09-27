using UIKit;

namespace MateMCP.Agent.Companion;

public class Program
{
    static void Main(string[] args)
    {
        // Boots with the MateMcpApplication subclass (instead of the default UIApplication)
        // so it can register application-wide UIKeyCommands for Tab/Shift+Tab focus
        // navigation. See MateMcpApplication and MateMcpNativeTabBridge. Refs #277.
        UIApplication.Main(args, typeof(MateMcpApplication), typeof(AppDelegate));
    }
}
