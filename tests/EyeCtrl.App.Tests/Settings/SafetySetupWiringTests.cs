namespace EyeCtrl.App.Tests.Settings;

using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// Source pins for the safety setup's composition seams. The behaviour lives in headlessly tested
/// seams (<c>SafetySetupStartup.MaybeRequest</c>, <c>SafetySetupLaunch.Run</c>, <c>SafetySetupHost</c>,
/// see SafetySetupFlowTests); what is left at the three call sites that only a real window and a
/// real startup exercise is the ARGUMENTS they pass, so these pins match each argument exactly —
/// a negated, swapped or dropped argument fails, not only a deleted call (A2 review M2: substring
/// scans let a negated hook and a flipped null check survive).
/// </summary>
public class SafetySetupWiringTests
{
    private static string Read(params string[] relative)
    {
        var parts = new List<string> { SourceScanner.RepoRoot().FullName };
        parts.AddRange(relative);
        return File.ReadAllText(Path.Combine(parts.ToArray())).Replace("\r\n", "\n");
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ");

    [Fact]
    public void Startup_RaisesTheRequestThroughTheSeam_WithTheExactArguments_AfterTheEnsure()
    {
        var source = Read("src", "EyeCtrl.App", "App.xaml.cs");
        var flat = Squash(source);
        const string call =
            "SafetySetupStartup.MaybeRequest( settings.General.SafetySetupDone, IsPausedLaunch, IsTestMode, messenger);";

        Check.WithCustomMessage("The startup hook must be exactly one MaybeRequest call with the flag, "
                                + "IsPausedLaunch, IsTestMode and the app messenger, in that order.")
            .That(Regex.Matches(flat, Regex.Escape(call)).Count).IsEqualTo(1);
        Check.WithCustomMessage("The hook must run after the supervision toggle is handed over and the "
                                + "startup ensure is queued (A2 review L2), not on BeginInvoke ordering.")
            .That(flat.IndexOf(call, StringComparison.Ordinal))
            .IsStrictlyGreaterThan(flat.IndexOf("supervisionToggle.RequestEnsure();", StringComparison.Ordinal));
        Check.That(source).Not.Contains("new SafetySetupRequestedMessage()");
    }

    [Fact]
    public void TheCoordinator_DispatchesThroughTheLaunchSeam_WithTheRealWindowState()
    {
        var source = Read("src", "EyeCtrl.App", "Services", "Coordinators", "SettingsUiCoordinator.cs");
        var start = source.IndexOf("private void ShowSafetySetup()", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var body = Squash(source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)]);

        Check.That(source).Contains("Register<SettingsUiCoordinator, SafetySetupRequestedMessage>");
        Check.That(body).Contains(
            "SafetySetupLaunch.Run( windowOpen: _settingsWindow is not null, openWindow: OpenSettingsWindow, "
            + "start: closeWhenDone => _settingsWindow?.StartSafetySetup(closeWhenDone));");
    }

    [Fact]
    public void TheWindow_WiresTheHost_TheFlow_TheRowAndTheClose()
    {
        var flat = Squash(Read("src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));

        Check.WithCustomMessage("The row re-runs the chain and keeps the window open.")
            .That(flat).Contains("RunSafetySetup = () => StartSafetySetup(closeWhenDone: false),");
        Check.WithCustomMessage("The host must ask the modal's live visibility and close the window deferred.")
            .That(flat).Contains(
                "new SafetySetupHost( () => _viewModel.IsModalVisible, CreateSafetySetupFlow, "
                + "() => Dispatcher.BeginInvoke(new Action(Close)));");
        Check.WithCustomMessage("The chain saves through the window's ApplyAndSave, switches the view through "
                                + "SwitchSettingsUiMode and reports its end to the host.")
            .That(flat).Contains("ApplyAndSave, SwitchSettingsUiMode, onEnded);");
        Check.That(flat).Contains("internal void StartSafetySetup(bool closeWhenDone) { if (_safetySetup.Start(closeWhenDone))");

        var closed = flat.IndexOf("Closed += (_, _) =>", StringComparison.Ordinal);
        var closedEnd = flat.IndexOf("_viewModel.Dispose(); };", closed, StringComparison.Ordinal);
        Check.That(closed).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("The window's close path must abandon the chain (a late crash-recovery "
                                + "result must not reach the disposed view model).")
            .That(flat[closed..closedEnd]).Contains("_safetySetup.OnWindowClosed();");
    }
}
