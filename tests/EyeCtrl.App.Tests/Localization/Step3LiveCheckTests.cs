namespace EyeCtrl.App.Tests.Localization;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// T13's supervised live check: the Step-3 gaze surfaces on the REAL screen with the shipped German.
/// The HARNESS navigates — the owner finds steady fixation hard, so no step asks them to click or
/// dwell; nothing is simulated input, and no gaze is fed to any detector.
///
/// <para>
/// <b>Safety.</b> The calibration window runs on a TEMP calibration path with its view model left
/// Idle, so no session starts, nothing writes <c>calibration.json</c> (not even the temp one) and its
/// Retry/Accept buttons are inert; the banner's snooze timer and the exercise's frame timer are
/// stopped and every window is closed in a <c>finally</c>; the action ring's layer change is driven
/// through the view model's own activation seam without any gaze or OS click, and nothing the owner
/// could click has an effect on the machine (the ring's dwell targets are gaze-driven, and no gaze is
/// fed). Every window is topmost, and <see cref="LiveCheck.AssertVisible"/> +
/// <see cref="LiveCheck.AssertShowsText"/> prove each step is on screen before its hold.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> The class is <c>Category=UI</c> (CI's <c>Category!=UI</c> filter excludes it)
/// and every test SKIPS unless <c>EYECTRL_LIVE_CHECK=1</c>. Run one case at a time, with the owner's
/// go-ahead: <c>dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~&lt;case&gt;"</c>.
/// The viewer order is the declaration order below: the ring (labels after Help, then the More ring),
/// the modifier bar, the calibration screens (running point, good completion, poor result, wrapped
/// failure), the eye-care banner and exercise, then the scroll and backdrop hints. The holds total
/// about 2 min 20 s.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class Step3LiveCheckTests
{
    private static readonly TimeSpan RingHold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MoreRingHold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BarHold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RunningPointHold = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Production auto-dismisses the good completion after 3 s (<c>CalibrationCompletion.GoodAutoDismissMs</c>),
    /// so this step shows that lifetime rather than outliving it.
    /// </summary>
    private static readonly TimeSpan GoodCompletionHold = TimeSpan.FromSeconds(2.5);

    /// <summary>A poor or failed panel lingers 12 s (<c>PoorAutoDismissMs</c>); the hold stays inside it.</summary>
    private static readonly TimeSpan PoorResultHold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailureHold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BannerHold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExerciseHold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HintHold = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The action ring, labels on — the state after the Help button has been looked at — then the
    /// "Mehr" ring. The labels come from the shipped German through the live view model; the layer
    /// change goes through the view model's own activation seam (what a dwell on "Mehr" runs), with
    /// no gaze and no OS input. The window's preview capture reads the screen (it bakes nothing in;
    /// the overlays exclude themselves from capture).
    /// </summary>
    [StaFact]
    public void TheActionRing_ShowsItsGermanLabels_AndTheMehrRing()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        ActionMenuWindow? window = null;
        ActionMenuViewModel? vm = null;
        try
        {
            var settings = new EyeCtrlSettings();
            vm = new ActionMenuViewModel(settings, new WeakReferenceMessenger());
            window = new ActionMenuWindow { DataContext = vm };
            var centre = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth / 2),
                DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight / 2));
            var actions = Enum.GetValues<InteractionAction>().ToList();

            void ShowRing() => window!.Show(centre, actions, vm!.ShowBack, dwellTimeMs: 600,
                vm.EffectiveRingRadius, settings.Cursor.CircleSize, settings.Cursor.CircleColor,
                settings.Cursor.PositionIndicatorStyle, vm.ManualSelectionCenter, vm.ManualSelectionRadius,
                vm.GazePoint, vm.ShowLabels);

            // Production rebuilds the ring when the layer or the Help state changes
            // (OverlayVmBindingCoordinator.OnActionMenuVMPropertyChanged).
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ActionMenuViewModel.RingVersion) or nameof(ActionMenuViewModel.ShowLabels))
                    ShowRing();
            };

            vm.Receive(new ShowActionMenuMessage(centre, actions, ShowBack: false, DwellTimeMs: 600, centre, CircleSize: 250));
            vm.Receive(new ToggleActionMenuLabelsMessage()); // labels on, as after looking at Help
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            LiveCheck.AssertShowsText(window, "Action ring (labels, after Help)", "Linksklick", "Mehr");
            LiveCheck.AssertVisible(window, "Action ring (labels, after Help)");
            LiveCheck.PumpFor(RingHold);

            // What a dwell on "Mehr" runs — the layer change only; no gaze and no OS input.
            typeof(ActionMenuViewModel)
                .GetMethod("ActivateForTest", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, ["More"]);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            LiveCheck.AssertShowsText(window, "Action ring (the Mehr ring)", "Zurück", "Tastatur");
            LiveCheck.AssertVisible(window, "Action ring (the Mehr ring)");
            LiveCheck.PumpFor(MoreRingHold);
        }
        finally
        {
            try { window?.Close(); } catch { /* the window may already be gone */ }
            vm?.Dispose();
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>The modifier bar — the one labelled edge surface left (D8) — with its German captions.</summary>
    [StaFact]
    public void TheModifierBar_ShowsShiftStrgAlt()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        EdgeButtonBarWindow? window = null;
        try
        {
            var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
            window = new EdgeButtonBarWindow { DataContext = vm };
            vm.ButtonSetChanged += window.SyncButtons;

            // Production's own anchor and definition set (OverlayMessageCoordinator.ShowActionMenu).
            vm.ShowButtons(ActionMenuModifierButtons.Definitions(600, new HashSet<ModifierKey>()),
                ScreenGeometry.LeftEdgeAnchor(30, UIConstants.ButtonRadius));
            window.SyncButtons();
            window.Show();
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            LiveCheck.AssertShowsText(window, "Modifier bar", "Shift", "Strg", "Alt");
            LiveCheck.AssertVisible(window, "Modifier bar");
            LiveCheck.PumpFor(BarHold);
        }
        finally
        {
            try { window?.Hide(); } catch { /* the window may already be gone */ }
            try { window?.Close(); } catch { /* the window may already be gone */ }
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The calibration window's running point: the Collecting presentation production draws —
    /// the progress line, the pulse and the cancel hint — driven through the window's own
    /// <c>UpdateVisualState</c>. The view model stays Idle and its calibration path is in a temp
    /// directory: no session starts and nothing can write <c>calibration.json</c>.
    /// </summary>
    [StaFact]
    public void TheCalibrationWindow_ShowsTheRunningPoint()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        var dir = TempTestDirectory.Create("t13-live");
        CalibrationWindow? window = null;
        try
        {
            window = CalibrationWindowOn(new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth / 2),
                DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight / 2)), dir);
            window.UpdateVisualState(CalibrationState.Collecting, 0.4);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            LiveCheck.AssertShowsText(window, "Calibration (running point)", "Auf den Punkt blicken · 2 / 9", "Mit Esc abbrechen");
            LiveCheck.AssertVisible(window, "Calibration (running point)");
            LiveCheck.PumpFor(RunningPointHold);
        }
        finally
        {
            CloseCalibration(window);
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The good-completion panel: a clean fit's title and its German mean-error line. Production
    /// auto-dismisses it after 3 s, and the hold shows exactly that lifetime.
    /// </summary>
    [StaFact]
    public void TheCalibrationWindow_ShowsAGoodCompletion()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        var dir = TempTestDirectory.Create("t13-live");
        CalibrationWindow? window = null;
        try
        {
            window = CalibrationWindowOn(new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth / 2),
                DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight / 2)), dir);
            // The completion panel reads these from the view model; the state stays Idle, so the
            // buttons cannot act (Retry no-ops, Accept only closes).
            var vm = (CalibrationViewModel)window.DataContext;
            vm.MeanErrorPixels = 12.3;
            vm.QualityPercent = 1.2;
            window.UpdateVisualState(CalibrationState.Active, 1.0);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            LiveCheck.AssertShowsText(window, "Calibration (good completion)", "Kalibrierung abgeschlossen", "Mittlere Abweichung");
            LiveCheck.AssertVisible(window, "Calibration (good completion)");
            LiveCheck.PumpFor(GoodCompletionHold);
        }
        finally
        {
            CloseCalibration(window);
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>The poor-quality result: the same title with the poor-quality detail and the German Retry/Accept buttons.</summary>
    [StaFact]
    public void TheCalibrationWindow_ShowsThePoorResult_WithUebernehmenUndWiederholen()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        var dir = TempTestDirectory.Create("t13-live");
        CalibrationWindow? window = null;
        try
        {
            window = CalibrationWindowOn(new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth / 2),
                DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight / 2)), dir);
            var vm = (CalibrationViewModel)window.DataContext;
            vm.MeanErrorPixels = 41.7;
            vm.QualityPercent = 5.0;
            window.UpdateVisualState(CalibrationState.Active, 1.0);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            LiveCheck.AssertShowsText(window, "Calibration (poor result)", "So übernehmen", "Wiederholen");
            LiveCheck.AssertVisible(window, "Calibration (poor result)");
            LiveCheck.PumpFor(PoorResultHold);
        }
        finally
        {
            CloseCalibration(window);
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The quality-failure line with two failing points — the case the line wraps for — through the
    /// real failed presentation. The view model carries the typed failure, so the sentence is
    /// production's own <c>CalibrationFailureText</c> output.
    /// </summary>
    [StaFact]
    public void TheCalibrationWindow_ShowsTheWrappedQualityFailure()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        var dir = TempTestDirectory.Create("t13-live");
        CalibrationWindow? window = null;
        try
        {
            window = CalibrationWindowOn(new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth / 2),
                DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight / 2)), dir);
            var vm = (CalibrationViewModel)window.DataContext;
            var regions = Enum.GetValues<CalibrationTargetRegion>()
                .Where(r => r != CalibrationTargetRegion.Unknown).ToArray();
            vm.Failure = new CalibrationFailure(CalibrationFailureCode.Quality,
                (System.Collections.Generic.IReadOnlyList<CalibrationQualityFailure>)
                [
                    new CalibrationQualityFailure(0, regions[0], CalibrationQualityReason.ExcessScatter, 41.0, 30.0),
                    new CalibrationQualityFailure(1, regions[6], CalibrationQualityReason.ResidualTooLarge, 140.0, 110.0),
                ]);
            window.UpdateVisualState(CalibrationState.Failed, 1.0);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            LiveCheck.AssertShowsText(window, "Calibration (wrapped quality failure)",
                "Kalibrierung fehlgeschlagen", "Kalibrierung nicht übernommen");
            LiveCheck.AssertVisible(window, "Calibration (wrapped quality failure)");
            LiveCheck.PumpFor(FailureHold);
        }
        finally
        {
            CloseCalibration(window);
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The eye-care banner through its production show path, <c>ShowAsync</c>, on a private messenger
    /// (so its active-message never reaches the app's bus) with no dwell registry; the snooze timer is
    /// stopped by <c>Cancel</c> in the finally and no gaze is fed, so nothing dwells on its own.
    /// </summary>
    [StaFact]
    public void TheEyeCareBanner_ShowsItsGermanMessage()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        EyeCareBannerWindow? window = null;
        try
        {
            window = new EyeCareBannerWindow(messenger: new WeakReferenceMessenger());
            var centre = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth / 2),
                DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight / 2));
            _ = window.ShowAsync(StringTable.Current.T("eye-care.banner.message"), centre, autoSnoozeSeconds: 600);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            LiveCheck.AssertShowsText(window, "Eye-care banner", "Zeit für eine Augenpause", "starten", "später");
            LiveCheck.AssertVisible(window, "Eye-care banner");
            LiveCheck.PumpFor(BannerHold);
        }
        finally
        {
            try { window?.Cancel(); } catch { /* the window may already be gone */ }
            try { window?.Close(); } catch { /* the window may already be gone */ }
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The exercise screen through its production <c>RunAsync</c>: the German exercise name, the
    /// countdown and the "Ende ✕" skip button. The frame timer is stopped by <c>Cancel</c> in the
    /// finally; the target getter is a fixed point and the recorder is a no-op — no gaze is involved.
    /// </summary>
    [StaFact]
    public void TheExerciseScreen_ShowsTheGermanCaptions()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        EyeCareExerciseWindow? window = null;
        try
        {
            window = new EyeCareExerciseWindow();
            var centre = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth / 2),
                DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight / 2));
            _ = window.RunAsync(StringTable.Current.T("eye-care.exercise.smoothPursuit.name"),
                durationMicroseconds: 600_000_000, _ => centre, (_, _) => { });
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            LiveCheck.AssertShowsText(window, "Eye-care exercise", "Gleitendes Folgen", "Ende ✕");
            LiveCheck.AssertVisible(window, "Eye-care exercise");
            LiveCheck.PumpFor(ExerciseHold);
        }
        finally
        {
            try { window?.Cancel(); } catch { /* the window may already be gone */ }
            try { window?.Close(); } catch { /* the window may already be gone */ }
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>The scroll overlay through its production <c>Show(vm)</c>: the anchor marks and the German stop hint.</summary>
    [StaFact]
    public void TheScrollOverlay_ShowsTheGermanHint()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        ScrollOverlayWindow? window = null;
        try
        {
            var vm = new ScrollOverlayViewModel(new WeakReferenceMessenger(), new ScrollSettings(), new DwellSettings());
            var anchor = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth / 2),
                DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight / 2));
            vm.EnterScrollMode(anchor, new PhysicalPoint(anchor.X + DpiHelper.LogicalToPhysicalX(200), anchor.Y));
            window = new ScrollOverlayWindow();
            window.Show(vm);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            LiveCheck.AssertShowsText(window, "Scroll overlay", "Mit Esc das Scrollen beenden");
            LiveCheck.AssertVisible(window, "Scroll overlay");
            LiveCheck.PumpFor(HintHold);
        }
        finally
        {
            try { window?.Close(); } catch { /* the window may already be gone */ }
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>The glass backdrop's dismiss hint (with its hide button drawn, as production shows it).</summary>
    [StaFact]
    public void TheGlassBackdrop_ShowsTheGermanHint()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        GlassBackdropWindow? window = null;
        try
        {
            window = new GlassBackdropWindow();
            var centre = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth - 120),
                DpiHelper.LogicalToPhysicalY(120));
            window.DrawHideButton(centre);
            window.ShowEscapeHint();
            window.Show();
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
            LiveCheck.AssertShowsText(window, "Glass backdrop", "Mit Esc beenden");
            LiveCheck.AssertVisible(window, "Glass backdrop");
            LiveCheck.PumpFor(HintHold);
        }
        finally
        {
            try { window?.Hide(); } catch { /* the window may already be gone */ }
            try { window?.Close(); } catch { /* the window may already be gone */ }
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// A real calibration window on a TEMP calibration path, its view model left Idle: the showcase
    /// drives the window's presentation directly, so no session starts and no file is written.
    /// </summary>
    private static CalibrationWindow CalibrationWindowOn(PhysicalPoint centre, string root)
    {
        var vm = new CalibrationViewModel(
            new CalibrationSettings(),
            new CalibrationService(),
            new WeakReferenceMessenger(),
            new TestFakes.FakeGazeProvider(),
            new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth),
                DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight)),
            monitorDeviceName: "live-check",
            calibrationFilePath: Path.Combine(root, "calibration.json"));
        vm.TargetPositions = [centre];
        vm.TotalTargets = 9;
        vm.CurrentTargetIndex = 1;
        vm.CurrentTargetPosition = centre;

        var window = new CalibrationWindow();
        window.SetViewModel(vm);
        return window;
    }

    /// <summary>Hides the calibration window without touching the view model (it is Idle, so a close is all that is left).</summary>
    private static void CloseCalibration(CalibrationWindow? window)
    {
        try { window?.DismissCalibration(); } catch { /* the window may already be gone */ }
        try { window?.Close(); } catch { /* the window may already be gone */ }
    }
}
