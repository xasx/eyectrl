namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.Core.Enums;

public partial class SplashWindow : OverlayWindow
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Whether the splash says "no eye tracker found" (Wave B B5): only when the provider FELL BACK
    /// to the mouse — a tracker was asked for (Auto, or a Tobii type) and the mouse is what resolved.
    /// Never when the mouse was chosen explicitly, and never in test mode, whose provider is always
    /// the mouse.
    /// </summary>
    /// <param name="requested">The provider type the settings asked for.</param>
    /// <param name="resolved">The provider type actually in use (decorators unwrapped).</param>
    /// <param name="isTestMode">Whether the app runs in test mode.</param>
    /// <returns>True when the sentence should show.</returns>
    internal static bool ShowsNoTrackerSentence(GazeProviderType requested, GazeProviderType resolved, bool isTestMode) =>
        !isTestMode && requested != GazeProviderType.Mouse && resolved == GazeProviderType.Mouse;

    /// <summary>Shows the no-tracker sentence under the progress bar.</summary>
    internal void ShowNoTrackerSentence() => NoTrackerSentence.Visibility = Visibility.Visible;
}
