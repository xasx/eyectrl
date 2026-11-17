namespace EyeCtrl.App.Tests.Settings;

using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// Source pins for the first-run guide's composition seams (Task 12, after the safety-setup
/// machinery retired). The raise behaviour lives in headlessly testable seams
/// (<c>FirstRunGuideFlow</c>, <c>SafetySetupFlow</c>, see SafetySetupFlowTests); what is left at
/// the call sites that only a real window and a real startup exercise is the ARGUMENTS they pass,
/// so these pins match each argument exactly — a negated, swapped or dropped argument fails, not
/// only a deleted call (A2 review M2: substring scans let a negated hook and a flipped null check
/// survive).
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
    public void TheCoordinator_RegistersTheRenamedMessage_AndCarriesThePerRunFactory()
    {
        var source = Read("src", "EyeCtrl.App", "Services", "Coordinators", "SettingsUiCoordinator.cs");

        Check.WithCustomMessage("The guide request (startup and the Settings → General row) must "
                                + "register under its renamed type — the old record is deleted.")
            .That(source).Contains("Register<SettingsUiCoordinator, FirstRunGuideRequestedMessage>");

        // The per-run factory: the one-flow gate and its lease, the raise, its unwind, and the
        // status-card fallback the unwind invokes. Each named explicitly so a deletion or a
        // rename shows up as a missing literal, not a silent behavioural change.
        Check.That(source).Contains("private FirstRunGuideHost? _guideHost;");
        Check.That(source).Contains("private SplashThreadHandle? _splashHandle;");
        Check.That(source).Contains("private FirstRunGuideWindow? _guideWindow;");
        Check.That(source).Contains("private FirstRunGuideViewModel? _guideVm;");
        Check.That(source).Contains("private FirstRunGuideFlow? _guideFlow;");
        Check.That(source).Contains("private GuideSurface? _guideSurface;");
        Check.That(source).Contains("private Action<bool>? _onToolbarVisibility;");
        Check.That(source).Contains("private void StartFirstRunGuide()");
        Check.WithCustomMessage("The raise-failure unwind must take the same context+lease shape the "
                                + "raise hands it, so it can undo Show and release the gate.")
            .That(source).Contains(
                "private void UnwindPartialRaise(EventCoordinationContext context, FirstRunGuideLease lease)");
        Check.That(source).Contains(
            "private void BuildAndRaiseGuide(EventCoordinationContext context, FirstRunGuideLease lease)");
        Check.WithCustomMessage("App assigns the status-card fallback through the EventCoordinator "
                                + "forwarder; the unwind invokes it when the raise failed.")
            .That(source).Contains("internal Action? StatusCardsFallback { get; set; }");
        Check.WithCustomMessage("The splash handle must be handed in for the latched show (r1 F15).")
            .That(source).Contains("public void SetSplashHandle(SplashThreadHandle? handle)");
    }

    [Fact]
    public void TheRetiredSafetySetupMachinery_IsGone()
    {
        var coordinator = Read("src", "EyeCtrl.App", "Services", "Coordinators", "SettingsUiCoordinator.cs");
        var window = Read("src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs");

        foreach (var retired in new[] { "SafetySetupLaunch.Run", "SafetySetupHost", "RunSafetySetup" })
        {
            Check.WithCustomMessage($"{retired} must be gone from SettingsUiCoordinator — the guide "
                                    + "raise replaced the safety-setup launch.")
                .That(coordinator).Not.Contains(retired);
            Check.WithCustomMessage($"{retired} must be gone from SettingsWindow — the window no "
                                    + "longer hosts the chain.")
                .That(window).Not.Contains(retired);
        }

        // The window's close path must still reset the latch release margin (a DPI correctness pin
        // that outlived the retired host) — the guard keeps the targets' release margin physical.
        Check.WithCustomMessage("The latch's release margin must be set in the targets' physical pixels "
                                + "(review 02 N1); without it the latch runs on the DPI-1.0 default.")
            .That(Squash(window)).Contains(
                "_viewModel.LatchReleaseMarginPhysical = DpiHelper.LogicalToPhysicalX(SettingsWindowViewModel.LatchReleaseMarginDip);");
    }
}
