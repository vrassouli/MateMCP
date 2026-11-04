using Foundation;
using Microsoft.Maui;

namespace MateMCP.Agent.Companion;

/// <summary>
/// Provides the MAUI window through UIKit's scene-based lifecycle.
/// Keep the registration name in sync with MacCatalyst/Info.plist.
/// Refs #352.
/// </summary>
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}
