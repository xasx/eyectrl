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

    [Fact]
    public void TheSetupNoticeWatch_DoesNotStartAtConstruction_WhileTheGuideIsPending()
    {
        // S2-1: the watch used to Start() unconditionally at construction — its immediate check
        // ran with GuideOpen=false (the guide opens only after the splash exits), so an unseen
        // failed-update record burned its shown-marker and the notice was then taken down behind
        // the guide's window without ever being seen. The construction-time start must sit behind
        // the guide-pending negation and the deferred start must live in the guide-closed handler
        // the raise-failure fallback doubles as. Comment-stripped (B9 M2): a comment that merely
        // mentions the calls must not satisfy the pin.
        var app = Squash(SourceScanner.StripComments(Read("src", "EyeCtrl.App", "App.xaml.cs")));

        Check.WithCustomMessage("The construction-time Start must be gated on !ShouldPrompt — a "
                                + "pending guide defers the watch into the guide-closed path.")
            .That(app).Contains(
                "if (!SafetySetupStartup.ShouldPrompt( settings.General.SafetySetupDone, IsPausedLaunch, IsTestMode)) _setupNoticeWatch.Start();");

        Check.WithCustomMessage("The guide-closed handler must start the deferred watch (it also "
                                + "runs via the raise-failure fallback).")
            .That(app).Contains(
                "void OnGuideClosedForStatusCards(bool open) { if (open) return; _eventCoordinator.GuideOpenChanged -= OnGuideClosedForStatusCards; _setupNoticeWatch?.Start(); _serviceProvider.GetRequiredService<StatusCardPresenter>().Start(); }");

        Check.WithCustomMessage("The watch must start in exactly two places: gated at construction "
                                + "and deferred in the guide-closed handler — a third site would "
                                + "re-burn the marker behind the guide.")
            .That(Regex.Matches(app, @"_setupNoticeWatch\s*\??\.Start\s*\(\s*\)")).HasSize(2);
    }

    [Fact]
    public void TheGuideCalibrationLifecycle_StandsDownAndReclaimsTheGuideClaims_InStepWithTheFlag()
    {
        // Ruling D3 (PR-review n1): "Calibrate now" from the guide must not strand the guide's
        // claims over the calibration surface — the result card's Retry/Accept are gaze-activation
        // targets, dead under the guide's fixation suppression, its offer suspension and the ring
        // gate. Collecting/Training releases the claim and sets the flag; Idle/Cancelled clears
        // the flag and re-acquires BEFORE ShowGuide, and only while the guide is still up (a guide
        // that ended mid-calibration already released in Restore — a blind re-acquire would leak
        // the claim past its death). Comment-stripped (B9 M2): a comment that merely mentions the
        // calls must not satisfy the pin.
        var flat = Squash(SourceScanner.StripComments(Read(
            "src", "EyeCtrl.App", "Services", "Coordinators", "SettingsUiCoordinator.cs")));

        // The StartCalibration body: from its declaration to the request send that ends it.
        var start = flat.IndexOf("private void StartCalibration()", StringComparison.Ordinal);
        var send = flat.IndexOf(
            "context.Messenger.Send(new CalibrationRequestedMessage());", StringComparison.Ordinal);
        Check.WithCustomMessage("StartCalibration and its closing request send must both exist")
            .That(start >= 0 && send > start).IsTrue();
        var method = flat[start..send];

        // Collecting/Training: the flag goes up and the guide's suppression claim comes down,
        // together in that branch.
        var collecting = method.IndexOf(
            "if (m.State is CalibrationState.Collecting or CalibrationState.Training)",
            StringComparison.Ordinal);
        var idle = method.IndexOf(
            "else if (m.State is CalibrationState.Idle or CalibrationState.Cancelled)",
            StringComparison.Ordinal);
        Check.WithCustomMessage("the calibration state branches must exist in this order")
            .That(collecting >= 0 && idle > collecting).IsTrue();
        var collectingBranch = method[collecting..idle];
        Check.WithCustomMessage("the Collecting branch must set the calibration flag")
            .That(collectingBranch.Contains("_modal.GuideCalibrationActive = true;")).IsTrue();
        Check.WithCustomMessage("the Collecting branch must release the guide's fixation suppression")
            .That(collectingBranch.Contains(
                "context.GazeEngine.ReleaseFixationSuppression(FixationSuppressor.Guide);")).IsTrue();

        // Idle/Cancelled: guarded on the guide still being up, flag cleared and claim re-acquired
        // BEFORE the guide window re-shows.
        var idleBranch = method[idle..];
        Check.WithCustomMessage("the re-claim must be guarded — a guide that ended mid-calibration "
                                + "already released, and a blind re-acquire leaks the claim")
            .That(idleBranch.Contains("if (_guideFlow is not null && _modal.GuideOpen)")).IsTrue();
        var flagClear = idleBranch.IndexOf(
            "_modal.GuideCalibrationActive = false;", StringComparison.Ordinal);
        var reacquire = idleBranch.IndexOf(
            "context.GazeEngine.AcquireFixationSuppression(FixationSuppressor.Guide);",
            StringComparison.Ordinal);
        var showGuide = idleBranch.IndexOf("_guideWindow?.ShowGuide();", StringComparison.Ordinal);
        Check.WithCustomMessage("the Idle/Cancelled branch must clear the flag, re-acquire, and "
                                + "only then re-show the guide")
            .That(flagClear >= 0 && reacquire > flagClear && showGuide > reacquire).IsTrue();

        // Every guide exit clears the flag too: the raise-failure unwind and the close restore.
        var unwind = flat.IndexOf("private void UnwindPartialRaise(", StringComparison.Ordinal);
        var restore = flat.IndexOf("void Restore(GuideExitReason reason)", StringComparison.Ordinal);
        Check.WithCustomMessage("the unwind and restore regions must exist in this order")
            .That(unwind >= 0 && restore > unwind).IsTrue();
        Check.WithCustomMessage("UnwindPartialRaise must clear the calibration flag")
            .That(flat[unwind..restore].Contains("_modal.GuideCalibrationActive = false;")).IsTrue();
        var restoreEnd = flat.IndexOf("_guideFlow.Closed += ", StringComparison.Ordinal);
        Check.WithCustomMessage("Restore must clear the calibration flag")
            .That(flat[restore..restoreEnd].Contains("_modal.GuideCalibrationActive = false;")).IsTrue();
    }
}
