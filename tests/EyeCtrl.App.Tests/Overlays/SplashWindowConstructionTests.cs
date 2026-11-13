// tests/EyeCtrl.App.Tests/Overlays/SplashWindowConstructionTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// The redesigned splash constructs headless (OverlayClientOriginTests also constructs it — new
/// XAML must parse with no Application resources) and carries the spec §4 contract: fixed card,
/// mark on the icon tile, reserved sentence row, click-through, no band-fallback order repair.
/// The window is never shown.
/// </summary>
[Collection("WpfApplication")]
public class SplashWindowConstructionTests
{
    [StaFact]
    public void TheWindow_CarriesTheSpecContract()
    {
        var splash = InvisibleTestWindow.Prepare(new SplashWindow());
        try
        {
            Check.That(splash.Width).IsEqualTo(SplashLayout.CardWidth);
            Check.That(splash.Height).IsEqualTo(SplashLayout.CardHeight);
            Check.That(splash.IsClickThrough).IsTrue();
            Check.That(splash.DeclaresNoOrderRepair).IsTrue();
            Check.That(splash.SentenceShown).IsFalse();
            Check.That(splash.NoTrackerSentence.Opacity).IsEqualTo(0);
            Check.That(splash.NoTrackerSentence.Margin).IsEqualTo(new Thickness(0));   // the 8 DIP gap lives below the title
            Check.That(System.Windows.Automation.AutomationProperties.GetAutomationId(splash.NoTrackerSentence))
                .IsEqualTo("Splash_NoTracker");
            // the mark's parts exist (names the storyboards target)
            Check.That(splash.FindName("DwellArc")).IsNotNull();
            Check.That(splash.FindName("GazeDot")).IsNotNull();
            Check.That(splash.FindName("Bloom")).IsNotNull();
            Check.That(splash.FindName("MarkHost")).IsNotNull();
            Check.That(splash.FindName("Tile")).IsNotNull();
            Check.That(splash.FindName("SplashContent")).IsNotNull();
        }
        finally { splash.Close(); }
    }

    /// <summary>
    /// Spec §8: the splash thread has NO Application resources — the window must construct on a
    /// bare STA thread. This is the resource-independence proof the r4 review asked the spike to
    /// carry; it belongs here where the real XAML is under test.
    /// </summary>
    [Fact]
    public void TheWindow_ConstructsOnABareStaThread_WithoutApplicationResources()
    {
        Exception? failure = null;
        var constructed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                // no WpfApplicationGate, no Application — Prepare is posture for the no-visible-window
                // guard (property sets + a Win32 tag), not a resource source; the XAML still parses bare.
                var splash = InvisibleTestWindow.Prepare(new SplashWindow());
                Check.That(splash.NoTrackerSentence).IsNotNull();
                splash.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { constructed.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Check.That(constructed.Wait(5000)).IsTrue();
        Check.WithCustomMessage($"Splash did not construct without Application resources: {failure}").That(failure).IsNull();
    }
}
