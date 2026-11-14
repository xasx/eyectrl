// tests/EyeCtrl.App.Tests/Overlays/SplashWindowConstructionTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System.IO;
using System.Text.RegularExpressions;
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
    /// bare STA thread. HALF of the resource-independence proof, and on its own an overclaim:
    /// Application.Current is process-shared, so a StaticResource here would still RESOLVE and
    /// this test would stay green. What it proves is that nothing the XAML or the code-behind
    /// needs is MISSING without an Application; <see cref="TheSplashXaml_ReferencesNoApplicationResources"/>
    /// is the other half — it proves the XAML never ASKS for one. The two together close spec §8.
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

    /// <summary>
    /// The other half of the resource-independence proof (spec §8): the splash XAML contains NO
    /// StaticResource and NO DynamicResource — an App-level lookup on the splash thread would
    /// resolve against the process-shared Application.Current and silently depend on whatever
    /// the main thread merged, which is exactly what §8 forbids. Comments are stripped first:
    /// the XAML's explanatory comment names StaticResource, but a comment is not live code.
    /// </summary>
    [Fact]
    public void TheSplashXaml_ReferencesNoApplicationResources()
    {
        var path = Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Overlays", "SplashWindow.xaml");
        var xaml = SourceScanner.StripComments(File.ReadAllText(path));
        Check.WithCustomMessage("SplashWindow.xaml resolves an App-level StaticResource — the splash thread has no Application resources (spec §8)")
            .That(Regex.IsMatch(xaml, @"\bStaticResource\b")).IsFalse();
        Check.WithCustomMessage("SplashWindow.xaml resolves a DynamicResource — the splash thread has no Application resources (spec §8)")
            .That(Regex.IsMatch(xaml, @"\bDynamicResource\b")).IsFalse();
    }
}
