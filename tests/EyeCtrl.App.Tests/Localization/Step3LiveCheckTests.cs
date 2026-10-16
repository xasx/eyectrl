namespace EyeCtrl.App.Tests.Localization;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
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
        BeginShowcaseCase();
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
            EndShowcaseCase();
        }
    }

    /// <summary>The modifier bar — the one labelled edge surface left (D8) — with its German captions.</summary>
    [StaFact]
    public void TheModifierBar_ShowsShiftStrgAlt()
    {
        BeginShowcaseCase();
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
            EndShowcaseCase();
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
        BeginShowcaseCase();
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
            RemoveTemp(dir);
            EndShowcaseCase();
        }
    }

    /// <summary>
    /// The good-completion panel: a clean fit's title and its German mean-error line. Production
    /// auto-dismisses this panel after 3 s, so the hold shows exactly that lifetime — <b>expect this
    /// step to flash by</b> (about 2.5 s; the other screens hold 10–25 s).
    /// </summary>
    [StaFact]
    public void TheCalibrationWindow_ShowsAGoodCompletion()
    {
        BeginShowcaseCase();
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
            RemoveTemp(dir);
            EndShowcaseCase();
        }
    }

    /// <summary>The poor-quality result: the same title with the poor-quality detail and the German Retry/Accept buttons.</summary>
    [StaFact]
    public void TheCalibrationWindow_ShowsThePoorResult_WithUebernehmenUndWiederholen()
    {
        BeginShowcaseCase();
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
            RemoveTemp(dir);
            EndShowcaseCase();
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
        BeginShowcaseCase();
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
            RemoveTemp(dir);
            EndShowcaseCase();
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
        BeginShowcaseCase();
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
            EndShowcaseCase();
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
        BeginShowcaseCase();
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
            EndShowcaseCase();
        }
    }

    /// <summary>The scroll overlay through its production <c>Show(vm)</c>: the anchor marks and the German stop hint.</summary>
    [StaFact]
    public void TheScrollOverlay_ShowsTheGermanHint()
    {
        BeginShowcaseCase();
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
            AssertFullScreen(window, "Scroll overlay");
            LiveCheck.AssertShowsText(window, "Scroll overlay", "Mit Esc das Scrollen beenden");
            LiveCheck.AssertVisible(window, "Scroll overlay");
            LiveCheck.PumpFor(HintHold);
        }
        finally
        {
            try { window?.Close(); } catch { /* the window may already be gone */ }
            EndShowcaseCase();
        }
    }

    /// <summary>
    /// The glass backdrop through production's own show path — <c>OverlayVmBindingCoordinator.ShowBackdropIfEnabled</c>
    /// (<c>:96-117</c>: <c>Show()</c> → <c>ApplyBackdrop(settings)</c> → <c>DrawHideButton(centre)</c>) — with
    /// the owner's REAL material read read-only from <c>%APPDATA%\EyeCtrl\settings.json</c> (Mica today;
    /// the production default if it is missing), and the ✕ at the centre production computes
    /// (<c>:107-111</c>), never a hand-picked point. Proves the ✕ ("Schließen") is drawn exactly there and
    /// lies inside the screen, and the hint is on the screen too.
    /// </summary>
    [StaFact]
    public void TheGlassBackdrop_ShowsTheGermanHint()
    {
        BeginShowcaseCase();
        GlassBackdropWindow? window = null;
        try
        {
            var settings = BackdropSettingsOfTheOwner();
            settings.Enabled = true; // the showcase shows the backdrop itself; production skips only when disabled
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;

            // Production's own hide-button layout, restated from ShowBackdropIfEnabled (:107-111).
            var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
            var hideButtonCenter = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(dipRight),
                DpiHelper.LogicalToPhysicalY(UIConstants.BackdropHideButtonOffset));

            window = new GlassBackdropWindow();
            window.Show();                        // production order: Show, then the material, then the ✕
            window.ApplyBackdrop(settings);
            window.DrawHideButton(hideButtonCenter);
            window.ShowEscapeHint();
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));

            AssertFullScreen(window, "Glass backdrop");
            var glyph = ((Canvas)window.FindName("BackdropCanvas")!).Children.OfType<TextBlock>()
                .Single(t => t.Text == "\u2715");
            Check.WithCustomMessage("the ✕ must carry its German UIA name.")
                .That(AutomationProperties.GetName(glyph)).IsEqualTo("Schließen");
            var glyphCentre = glyph.PointToScreen(new Point(glyph.ActualWidth / 2, glyph.ActualHeight / 2));
            Check.WithCustomMessage(
                    $"the ✕ is drawn at ({glyphCentre.X:0.#}, {glyphCentre.Y:0.#}) px, not at production's "
                    + $"centre ({hideButtonCenter.X:0.#}, {hideButtonCenter.Y:0.#}).")
                .That(glyphCentre.X).IsCloseTo(hideButtonCenter.X, 1.5);
            Check.That(glyphCentre.Y).IsCloseTo(hideButtonCenter.Y, 1.5);

            var transform = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice;
            var physicalScreen = new Rect(0, 0,
                SystemParameters.VirtualScreenWidth * transform.M11,
                SystemParameters.VirtualScreenHeight * transform.M22);
            // The ✕'s INK, not its layout box: the box is the whole 100-DIP dwell disc, and what a
            // viewer must find on screen is the glyph. Since the 2026-10-13 corner choice (53 DIP
            // centre) the disc itself is fully inside too — pinned headless by
            // GlassBackdropHideButtonTests.TheHideButton_DiscAndHalo_LieFullyInsideTheScreen.
            var inkBox = LiveCheck.InkBoundsInDevicePixels(glyph);
            Check.WithCustomMessage(
                    $"the ✕'s ink {inkBox} (device px) leaves the screen {physicalScreen}; the layout box is the "
                    + "dwell disc, which the corner design may clip, but the glyph must be on screen.")
                .That(physicalScreen.Contains(inkBox)).IsTrue();

            LiveCheck.AssertShowsText(window, "Glass backdrop", "Mit Esc beenden");
            LiveCheck.AssertVisible(window, "Glass backdrop");
            LiveCheck.PumpFor(HintHold);
        }
        finally
        {
            try { window?.Hide(); } catch { /* the window may already be gone */ }
            try { window?.Close(); } catch { /* the window may already be gone */ }
            EndShowcaseCase();
        }
    }

    /// <summary>
    /// The common prologue: the opt-in gate, the WPF Application with its tokens, the DPI scale the
    /// App itself sets at startup, and the shipped German. Every case builds its window only after
    /// this, so every overlay is laid out at the user's real scale. Shared with
    /// <see cref="EyeCtrl.App.Tests.Overlays.BackdropLiveLookTests"/> (the backdrop live look),
    /// which starts exactly the way step 10 does.
    /// </summary>
    internal static void BeginShowcaseCase()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        InitializeDpiLikeTheApp();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
    }

    /// <summary>
    /// Puts the process-global DPI scale and the string table back to their test-run defaults. The
    /// assembly-level restore hooks do this after every test too; doing it here keeps the case's own
    /// window of exposure closed even when it fails early. Shared with
    /// <see cref="EyeCtrl.App.Tests.Overlays.BackdropLiveLookTests"/>.
    /// </summary>
    internal static void EndShowcaseCase()
    {
        DpiHelper.Initialize(1.0, 1.0);
        StringTable.ResetToEmbeddedEnglish();
    }

    /// <summary>
    /// The App's own startup DPI initialisation, replayed: <c>App.InitializeDpi</c>
    /// (<c>App.xaml.cs:694-703</c>) reads
    /// <c>PresentationSource.FromVisual(splash).CompositionTarget.TransformToDevice</c> and calls
    /// <c>DpiHelper.Initialize(m.M11, m.M22)</c>. A bare test host never runs it, so <c>DpiHelper</c>
    /// stays at its 1.0 default and every overlay that converts physical↔DIP (<c>CoverPrimaryScreen</c>,
    /// the ring's centre, <c>DrawHideButton</c>, the exercise dot, the scroll anchor…) lands at the
    /// wrong place or size — the distortion the owner saw in the live run. Same read, from an
    /// off-screen source on the same monitor.
    /// </summary>
    private static void InitializeDpiLikeTheApp()
    {
        using var source = new HwndSource(new HwndSourceParameters("t13-live-dpi")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });
        var transform = source.CompositionTarget!.TransformToDevice;
        DpiHelper.Initialize(transform.M11, transform.M22);
    }

    /// <summary>
    /// A full-screen overlay covers the primary screen exactly, now that the DPI scale is the App's:
    /// <c>CoverPrimaryScreen</c> divides the monitor's physical bounds by the scale, so 1920×1200 DIP
    /// at 2.0 on this screen. A wrong scale shows up here first.
    /// </summary>
    private static void AssertFullScreen(Window window, string name)
    {
        Check.WithCustomMessage(
                $"{name}: the window is {window.ActualWidth:0.#}×{window.ActualHeight:0.#} DIP, not the screen's "
                + $"{SystemParameters.PrimaryScreenWidth:0.#}×{SystemParameters.PrimaryScreenHeight:0.#}.")
            .That(window.ActualWidth).IsCloseTo(SystemParameters.PrimaryScreenWidth, 0.01);
        Check.That(window.ActualHeight).IsCloseTo(SystemParameters.PrimaryScreenHeight, 0.01);
    }

    /// <summary>
    /// The owner's backdrop settings, read READ-ONLY from <c>%APPDATA%\EyeCtrl\settings.json</c>: the
    /// material type is theirs (Mica today). A missing file, an unreadable one (the App may hold it),
    /// or an unknown value falls back to the production defaults — nothing here writes. Shared with
    /// <see cref="EyeCtrl.App.Tests.Overlays.BackdropLiveLookTests"/>, which reads the same way.
    /// </summary>
    internal static BackdropSettings BackdropSettingsOfTheOwner()
    {
        var settings = new BackdropSettings();
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl", "settings.json");
            if (!File.Exists(path)) return settings;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("backdrop", out var backdrop)
                && backdrop.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && Enum.TryParse<BackdropType>(type.GetString(), ignoreCase: true, out var parsed))
            {
                settings.Type = parsed;
            }
        }
        catch (IOException)
        {
            // The App may hold the file open; the production default is the fallback.
        }
        catch (JsonException)
        {
        }

        return settings;
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

    /// <summary>
    /// Removes a calibration case's temp directory (review-01 nit): four empty folders per run is
    /// litter, and the host's exit hook would only clean them at process end. Best effort — failing
    /// to delete a temp folder must not fail the check.
    /// </summary>
    private static void RemoveTemp(string directory)
    {
        try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
    }
}
