namespace EyeCtrl.App.Tests.Overlays;

using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;

/// <summary>
/// B5: the splash says "no eye tracker found" only when the provider FELL BACK to the mouse — never
/// when the mouse was chosen, never when a tracker resolved, never in test mode. The window is
/// constructed, never shown.
/// </summary>
[Collection("WpfApplication")]
public class SplashNoTrackerSentenceTests
{
    [Theory]
    [InlineData(GazeProviderType.Auto, GazeProviderType.Mouse, false, true)]           // the fallback
    [InlineData(GazeProviderType.StreamEngine, GazeProviderType.Mouse, false, true)]   // a Tobii type that fell back
    [InlineData(GazeProviderType.Mouse, GazeProviderType.Mouse, false, false)]         // chosen explicitly
    [InlineData(GazeProviderType.Auto, GazeProviderType.StreamEngine, false, false)]   // a tracker was found
    [InlineData(GazeProviderType.StreamEngine, GazeProviderType.StreamEngine, false, false)]
    [InlineData(GazeProviderType.Auto, GazeProviderType.Mouse, true, false)]           // test mode
    public void TheSentence_ShowsOnlyOnAFallback(
        GazeProviderType requested, GazeProviderType resolved, bool isTestMode, bool shows)
    {
        Check.That(SplashWindow.ShowsNoTrackerSentence(requested, resolved, isTestMode)).IsEqualTo(shows);
    }

    [StaFact]
    public void TheSentence_IsHiddenUntilAsked_AndReadsItsString()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var splash = InvisibleTestWindow.Prepare(new SplashWindow()); // never shown
        try
        {
            Check.That(splash.NoTrackerSentence.Visibility).IsEqualTo(Visibility.Collapsed);
            Check.That(splash.NoTrackerSentence.Text).IsEqualTo(StringTable.Current.T("overlays.splash.noTracker"));

            splash.ShowNoTrackerSentence();

            Check.That(splash.NoTrackerSentence.Visibility).IsEqualTo(Visibility.Visible);
        }
        finally { splash.Close(); }
    }

    [Fact]
    public void TheApp_AsksForTheSentence_OnlyThroughTheFallbackPredicate()
    {
        var app = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "App.xaml.cs"));
        Check.WithCustomMessage("App.OnStartup must gate the splash sentence on the fallback predicate with the REQUESTED type")
            .That(Regex.IsMatch(app,
                @"SplashWindow\s*\.\s*ShowsNoTrackerSentence\s*\(\s*settings\s*\.\s*General\s*\.\s*GazeProviderType\s*,\s*activeProviderType\s*,\s*IsTestMode\s*\)\s*\)\s*splash\s*\.\s*ShowNoTrackerSentence\s*\(\s*\)")).IsTrue();
        Check.WithCustomMessage("ShowNoTrackerSentence is called more than once").That(
            Regex.Matches(app, @"\.ShowNoTrackerSentence\(").Count).IsEqualTo(1);
    }
}
