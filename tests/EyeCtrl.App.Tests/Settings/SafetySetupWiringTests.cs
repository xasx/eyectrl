namespace EyeCtrl.App.Tests.Settings;

using System.IO;

/// <summary>
/// Source pins for the safety setup's composition seams. The chain, the startup decision and the
/// row are unit-tested elsewhere; these pin the three connections that only a real window and a
/// real startup exercise, each of which a deletion would silently kill: the startup hook asks the
/// extracted decision, the coordinator opens the window and starts the chain, and the window gives
/// the chain its own apply+save and mode switch and hands the General row its entry point.
/// </summary>
public class SafetySetupWiringTests
{
    private static string Read(params string[] relative)
    {
        var parts = new List<string> { SourceScanner.RepoRoot().FullName };
        parts.AddRange(relative);
        return File.ReadAllText(Path.Combine(parts.ToArray())).Replace("\r\n", "\n");
    }

    [Fact]
    public void Startup_AsksTheExtractedDecision_AfterTheGazeEngineStarts()
    {
        var source = Read("src", "EyeCtrl.App", "App.xaml.cs");
        var start = source.IndexOf("gazeEngine.Start();", StringComparison.Ordinal);
        var hook = source.IndexOf(
            "SafetySetupStartup.ShouldPrompt(settings.General.SafetySetupDone, IsPausedLaunch, IsTestMode)",
            StringComparison.Ordinal);

        Check.That(start).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The startup hook must ask SafetySetupStartup.ShouldPrompt with the flag, "
                                + "IsPausedLaunch and IsTestMode, after gazeEngine.Start().")
            .That(hook).IsStrictlyGreaterThan(start);
        Check.That(source[hook..(hook + 400)]).Contains("new SafetySetupRequestedMessage()");
    }

    [Fact]
    public void TheCoordinator_OpensTheWindowAndStartsTheChainOnIt()
    {
        var source = Read("src", "EyeCtrl.App", "Services", "Coordinators", "SettingsUiCoordinator.cs");

        Check.That(source).Contains("Register<SettingsUiCoordinator, SafetySetupRequestedMessage>");
        var start = source.IndexOf("private void ShowSafetySetup()", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];
        Check.That(body).Contains("OpenSettingsWindow()");
        Check.That(body).Contains("StartSafetySetup()");
    }

    [Fact]
    public void TheWindow_FeedsTheChainItsOwnSaveAndModeSwitch_AndTheRowItsEntryPoint()
    {
        var source = Read("src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs");

        Check.WithCustomMessage("The General row's entry point must be the window's StartSafetySetup.")
            .That(source).Contains("RunSafetySetup = StartSafetySetup");

        var start = source.IndexOf("internal void StartSafetySetup()", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];
        Check.WithCustomMessage("The chain must save through the window's ApplyAndSave and switch the view "
                                + "through SwitchSettingsUiMode (it re-renders the pages and the toggle).")
            .That(body).Contains("ApplyAndSave, SwitchSettingsUiMode");
        Check.That(body).Contains("_safetySetup.Start()");
    }
}
