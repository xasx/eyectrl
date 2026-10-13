namespace EyeCtrl.App.Diagnostics.SubPixel;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Serilog;

/// <summary>
/// C6 — the sub-pixel probe's own full-screen window. An <see cref="OverlayWindow"/> subclass, so it
/// inherits the overlay font and the NOACTIVATE/TOOLWINDOW ex-styles; it is opaque, not
/// click-through, excluded from neither capture nor Alt-Tab, and never activates.
///
/// <para>
/// The window only renders what C5 tells it (<see cref="SubPixelProbeViewModel.Phase"/> and the
/// current presentation) and registers C5's seven dwell targets with their physical hit
/// rectangles (each visible button grown by its D3 margin).
/// Its close path invokes the session-end callback captured at construction — never a coordinator
/// field — and sets <see cref="ClosedObserved"/> before it does, so a re-entrant close is a no-op
/// and "already closed" means the event fired (review 05 F3/F4).
/// </para>
///
/// <para>
/// <b>SP-4c usability (the owner's live check).</b> Every button draws the app's standard
/// rectangular dwell cue (<see cref="DwellOutlineOverlay"/>, the keyboard/emoji/prediction outline)
/// driven from C5's <c>GetProgress</c>; the buttons are the D2 sizes with D3's forgiving hit
/// rectangles (+48 DIP per side, +16 for End test); and the Intro draws a looping motion demo of
/// the real ring so the owner sees what "moving" looks like before the run starts (D5).
/// </para>
///
/// <para>
/// <b>SP-4d legibility (SP-7 review 01 O1).</b> The Accent fill put every label at 1.08:1 in the
/// owner's Midnight theme — effectively blank rectangles — and made the Accent cue invisible on it
/// (1.00:1). The five formerly Accent-filled buttons now carry <c>SurfaceLight</c> with the same
/// <c>TextPrimary</c> label (8.03:1 Midnight, above the 4.5:1 floor in every registered theme);
/// "Later" keeps <c>SurfaceMedium</c>, which already passes both floors. End test keeps its
/// "danger / leave" identity as a 3 DIP <c>AccentDanger</c> rest border (3.63:1 against the fill)
/// rather than a fill, because an AccentDanger fill or label cannot reach 4.5:1 in every theme.
/// The cue — Accent against the light fills at 8.10:1 or better — is clipped inside the border, so
/// the growing outline cannot be mistaken for the rest edge. <b>Fixup 01 G1/G2</b> then centres
/// every label in its button (it was drawn at the top edge) and sizes them to the reading task:
/// 96 DIP for the answer digits, <c>FontSizeDisplay</c> for the screen buttons, <c>FontSizeTitle</c>
/// for End test — measured to fit each button's inner rectangle in EN and DE at 2.0. Everything
/// else (hit areas, text clearances, the stimulus, the verdict) is untouched.
/// </para>
///
/// <para>
/// Nothing here sets a snap or layout-rounding flag on the probe's ancestors (SP-2 review 01): the
/// backdrop is drawn 1:1 because the image is exactly screen-DIPs wide at the injected scale, and
/// the ring's placement is the only rounding in the subtree.
/// </para>
///
/// <para>
/// <b>SP-4e (amendment A1).</b> The interval corner digit ("1"/"2") takes the answers' 96 DIP in
/// <c>TextPrimary</c> — the owner could not tell the rings apart at the old 14 DIP
/// <c>TextSecondary</c> — anchored bottom-left with its box's bottom 24 DIP above the window's
/// edge, the trial label stacked above it. The schedule now insets trial starts by each block's
/// path travel, so the ring's maximum extent over any trial is the screen's middle half grown by
/// the travel and the ring radius; that extent and the corner digit's box clear each other by
/// hundreds of DIP (pinned by <c>SubPixelProbeCornerLabelTests</c>).
/// </para>
/// </summary>
internal sealed class SubPixelProbeWindow : OverlayWindow, IProbeWindow
{
    private const double AnswerWidth = 380;
    private const double AnswerHeight = 260;
    private const double ScreenButtonWidth = 420;
    private const double ScreenButtonHeight = 170;
    private const double EndButtonWidth = 280;
    private const double EndButtonHeight = 120;
    private const double DotDiameter = 14;

    /// <summary>
    /// D3 (amended by fixup 01): extra hit-AREA DIP around the answers and the screen buttons — the
    /// visible button grown by this on every side, so the owner's measured gaze error no longer has
    /// to land on the drawn button, without reaching up into the text a circle would swallow.
    /// </summary>
    internal const double ButtonHitMarginDip = 48;

    /// <summary>
    /// D3 (amended by fixup 01): extra hit-area DIP around End test. Smaller than
    /// <see cref="ButtonHitMarginDip"/> because a stray End test ends the run.
    /// </summary>
    internal const double EndButtonHitMarginDip = 16;

    /// <summary>
    /// SP-4d L2: End test's danger identity at rest, in DIP, as a border rather than a fill. An
    /// AccentDanger fill puts its TextPrimary label at 2.21–2.57:1, and an AccentDanger label on
    /// the new SurfaceLight fill reaches only 3.63–3.70:1 under Midnight and Ember — both below
    /// the 4.5:1 label floor. The border carries the identity at 3.63:1 or better against the
    /// fill, the label stays TextPrimary at 8.03:1 or better, and the cue (the overlay is the
    /// Border's child, clipped inside the border) draws above it.
    /// </summary>
    private const double EndButtonDangerBorderDip = 3;

    /// <summary>
    /// Fixup 01 G2: the answer digits' label size, in DIP (review 01 F1: 14 DIP glyphs at the top
    /// edge of a 380 × 260 DIP button are the weakest possible label for an owner who aims at the
    /// centre and finds fixation hard). 96 puts ~70 DIP of ink exactly where they aim; digits are
    /// identical in German, so there is no l10n risk. The word labels take the type ramp's two
    /// largest steps instead — <c>FontSizeDisplay</c> (34) for the 420 × 170 DIP screen buttons and
    /// <c>FontSizeTitle</c> (24) for End test's 280 × 120 DIP — so every label fits its inner
    /// rectangle (border + full-progress cue) in English and German at 2.0.
    ///
    /// <para>
    /// SP-4e T2 reuses the same size for the interval corner digit ("1"/"2"), which the owner could
    /// not tell apart at 14 DIP: it is the same reading task in the same corner, so the two sizes
    /// must not drift apart.
    /// </para>
    ///
    /// <para>
    /// A property, and public, because <c>RawGeometryLiteralTests</c> admits a <c>FontSize =</c>
    /// value only from <see cref="Tokens"/>, <c>ContainerRatio</c>, or a public static double on an
    /// <see cref="OverlayWindow"/> subclass — the same mechanism as
    /// <c>QuickActionsGridWindow.TileIconSize</c>. The class is internal, so this widens no real
    /// API.
    /// </para>
    /// </summary>
    public static double AnswerLabelFontSizeDip => 96;

    /// <summary>Fixup 01 G2: which label-size rule a probe button follows (see <see cref="BuildButton"/>).</summary>
    private enum ProbeLabelSize
    {
        /// <summary>The bare answer digits: <see cref="AnswerLabelFontSizeDip"/>.</summary>
        AnswerDigits,

        /// <summary>A word label on a 420 × 170 DIP screen button: <c>Tokens.FontSizeDisplay</c>.</summary>
        ScreenButton,

        /// <summary>End test on its 280 × 120 DIP button: <c>Tokens.FontSizeTitle</c>.</summary>
        EndTest,
    }

    /// <summary>The Ask screen's panel width: two 420 DIP buttons side by side plus their 120 DIP gap.</summary>
    private const double AskPanelWidth = 960;

    /// <summary>Fixup 01 F1: DIP between the Intro text block and the demo band's top.</summary>
    private const double IntroTextGapDip = 24;

    /// <summary>
    /// Fixup 01 F1 / fixup 02 R1: DIP between the demo band's bottom and the Start button's top
    /// edge. 128, not 80: Start's hit area grows 48 DIP above the button, so the band-to-hit-area
    /// clearance is 128 − 48 = 80 DIP — above the owner's measured 75–130 px gaze bias (37–65
    /// DIP), which matters because the Intro asks them to follow the ring for up to 80 s.
    /// <see cref="Layout"/> clamps the button so its whole grown hit area stays on-screen for a
    /// large ring, where this gap would push it off.
    /// </summary>
    private const double IntroStartGapDip = 128;

    /// <summary>
    /// Fixup 02 R1: DIP kept free below Start's grown hit area, so the clamp never runs it off the
    /// screen (the ring's own maximum, 500 DIP, leaves the unclamped 128 DIP gap no room on a
    /// 1080 DIP screen).
    /// </summary>
    private const double IntroBottomMarginDip = 24;

    /// <summary>
    /// Fixup 01 F1: DIP between a screen's last text line and its button's top edge. 96, so the
    /// grown hit area (48 DIP up) still clears the text by 48 DIP — above the required 24.
    /// </summary>
    private const double TextToButtonGapDip = 96;

    /// <summary>The Ask screen's gap between its two buttons (D3: their hit areas must not overlap).</summary>
    private const double AskButtonGapDip = 120;

    /// <summary>
    /// SP-4e T2: the corner digit's box is anchored by its BOTTOM this far above the window's
    /// bottom edge — the same 24 DIP margin End test's bottom edge keeps — and the trial label
    /// stacks above the digit's box by <see cref="CornerToTrialLabelGapDip"/>. Before T2 the 14 DIP
    /// label sat at <c>height - 62</c>; a 96 DIP digit at that offset would be clipped by the
    /// window's bottom edge, so both positions derive from the measured box.
    /// </summary>
    private const double CornerLabelBottomMarginDip = 24;

    /// <summary>SP-4e T2: DIP between the corner digit's box top and the trial label's box bottom.</summary>
    private const double CornerToTrialLabelGapDip = 8;

    // D5: the demo runs one scripted interval pair, with the same interval/gap lengths as a
    // trial, and repeats. Its step law is SubPixelAbDriftMotion's (0.25 physical px per 30 ms),
    // so the demo ring moves exactly as a trial's ring does.
    private const int DemoIntervalMs = SubPixelProbeViewModel.ScriptedIntervalMs;
    private const int DemoGapMs = SubPixelProbeViewModel.GapMs;
    private const long DemoCycleMs = 2L * (DemoIntervalMs + DemoGapMs);

    /// <summary>Physical pixels the demo ring travels in one demo interval (the trial law's step × ticks).</summary>
    private static readonly double DemoTravelPx = SubPixelAbDriftMotion.StepPixelsPerTick
        * (DemoIntervalMs / (double)SubPixelAbDriftMotion.TickMilliseconds);

    private readonly SubPixelProbeViewModel _vm;
    private readonly Action _sessionEnded;
    private readonly double _scale;
    private readonly Canvas _scene = new();
    private readonly Image _backdrop = new();
    private readonly ProbeRingVisual _ring;
    private readonly Ellipse _targetDot = new();
    private readonly TextBlock _caption = new();
    private readonly TextBlock _question = new();
    private readonly TextBlock _corner = new();
    private readonly TextBlock _trialLabel = new();
    private readonly StackPanel _introPanel;
    private readonly StackPanel _voidHoldPanel;
    private readonly StackPanel _askPanel;
    private readonly StackPanel _resultPanel;
    private readonly TextBlock _resultText = new();
    private readonly Border _answerOne;
    private readonly Border _answerTwo;
    private readonly Border _endTest;
    private readonly Border _introStart;
    private readonly Border _askYes;
    private readonly Border _askLater;
    private readonly Border _resultDone;
    private readonly DwellOutlineOverlay _answerOneCue;
    private readonly DwellOutlineOverlay _answerTwoCue;
    private readonly DwellOutlineOverlay _endTestCue;
    private readonly DwellOutlineOverlay _introStartCue;
    private readonly DwellOutlineOverlay _askYesCue;
    private readonly DwellOutlineOverlay _askLaterCue;
    private readonly DwellOutlineOverlay _resultDoneCue;

    private PhysicalPoint? _lastGaze;
    private long _lastFrameTicks;
    private readonly Action _viewFault;
    private bool _faulted;
    private bool _renderingAttached;
    private readonly Func<long> _frameTicks;
    private readonly double _frameTicksPerMs;
    private bool _closedObserved;

    /// <summary>
    /// D5's Intro demo drift: the step law of a trial's drift, re-anchored onto the screen centre
    /// (recomputed by <see cref="Layout"/>, so it follows the window size) and driven by
    /// <see cref="DrawIntroDemo"/> from the frame clock.
    /// </summary>
    private SubPixelAbDriftMotion _demoMotion = new(new PhysicalPoint(0, 0), SubPixelAbDirection.East);

    private long _demoAnchorTicks;
    private bool _demoAnchored;

    /// <summary>
    /// Builds the probe window for one run. The window is not shown here; C7 shows it through
    /// <see cref="IProbeWindow.Show"/>.
    /// </summary>
    /// <param name="backdrop">The full-screen capture, or <c>null</c> for a plain backdrop.</param>
    /// <param name="vm">The run's view model; its phase drives every screen.</param>
    /// <param name="cursorSettings">The ring look the probe draws with.</param>
    /// <param name="sessionEnded">Invoked once, when this window's close is observed.</param>
    /// <param name="viewFault">
    /// Invoked once when a frame, phase or layout callback throws (fixup 04 HR1). Required, with no
    /// no-op default: the production host must pass C7's posted fault path through, or a faulted run
    /// would freeze behind an opaque topmost window with no way out (fixup 05 F2).
    /// </param>
    /// <param name="scale">
    /// Device pixels per DIP. Production passes <c>null</c> and the window reads
    /// <see cref="DpiHelper.ScaleX"/>; the W6 hit tests inject 1.0 and 2.0 to prove the registered
    /// geometry is in the same physical units the gaze path uses (C4's injection pattern).
    /// </param>
    /// <param name="frameTicks">
    /// The frame clock, in raw ticks; production passes <c>null</c> and uses
    /// <see cref="Stopwatch.GetTimestamp"/>. The render-gap test injects a millisecond clock.
    /// </param>
    /// <param name="frameTicksPerMs">Raw ticks per millisecond for <paramref name="frameTicks"/>; 0 selects the Stopwatch rate.</param>
    public SubPixelProbeWindow(
        BitmapSource? backdrop,
        SubPixelProbeViewModel vm,
        CursorSettings cursorSettings,
        Action sessionEnded,
        Action viewFault,
        double? scale = null,
        Func<long>? frameTicks = null,
        double frameTicksPerMs = 0)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _sessionEnded = sessionEnded ?? throw new ArgumentNullException(nameof(sessionEnded));
        _viewFault = viewFault ?? throw new ArgumentNullException(nameof(viewFault));
        _scale = scale ?? (DpiHelper.ScaleX <= 0 ? 1.0 : DpiHelper.ScaleX);
        _frameTicks = frameTicks ?? Stopwatch.GetTimestamp;
        _frameTicksPerMs = frameTicksPerMs > 0 ? frameTicksPerMs : Stopwatch.Frequency / 1000.0;

        // C6's window properties (build plan C6): opaque, full-screen on the primary monitor,
        // topmost, never activated, not in the taskbar or Alt-Tab. Not click-through (the window
        // itself is what blocks the external dwell targets underneath).
        IsClickThrough = false;
        ExcludeFromScreenCapture = false;
        ShowActivated = false;
        ShowInTaskbar = false;
        AllowsTransparency = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        Cursor = Cursors.None;
        UseLayoutRounding = false;
        SnapsToDevicePixels = false;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        _scene.Background = Tokens.BrushSurfaceDark;
        Content = _scene;

        _backdrop.Stretch = Stretch.Fill;

        // Size the image from the bitmap, not from SystemParameters: one image pixel per device
        // pixel at the injected scale. At the owner's 2.0 that is 3840/2 DIP for a 3840-px capture,
        // so `Stretch.Fill` stays a 1:1 draw even when SystemParameters and DpiHelper disagree
        // (fixup 02 M2).
        if (backdrop is not null)
        {
            _backdrop.Source = backdrop;
            _backdrop.Width = backdrop.PixelWidth / _scale;
            _backdrop.Height = backdrop.PixelHeight / _scale;
        }
        else
        {
            _backdrop.Width = SystemParameters.PrimaryScreenWidth;
            _backdrop.Height = SystemParameters.PrimaryScreenHeight;
        }

        Add(_backdrop, 0, 0);

        _ring = new ProbeRingVisual(_scale, cursorSettings, ProbeRingVisual.QuietLookOpacity)
        {
            Visibility = Visibility.Collapsed,
        };
        _scene.Children.Add(_ring);

        _targetDot.Fill = Tokens.BrushAccent;
        _targetDot.Width = DotDiameter;
        _targetDot.Height = DotDiameter;
        _targetDot.Visibility = Visibility.Collapsed;
        _scene.Children.Add(_targetDot);

        _caption.Text = T("overlays.subpixel.caption");
        _caption.FontSize = Tokens.FontSizeBody;
        _caption.Foreground = Tokens.BrushTextDim;
        _caption.FontWeight = Tokens.FontWeightMedium;
        Add(_caption, 0, 8);
        _caption.Width = Width;
        _caption.TextAlignment = TextAlignment.Center;

        _question.FontSize = Tokens.FontSizeTitle;
        _question.FontWeight = Tokens.FontWeightStrong;
        _question.Foreground = Tokens.BrushTextPrimary;
        _question.TextAlignment = TextAlignment.Center;
        Add(_question, 0, 0);
        _question.Width = Width;

        // SP-4e T2: the interval digit takes the answers' 96 DIP and TextPrimary — the owner could
        // not tell "1" from "2" at the old 14 DIP TextSecondary — and stays bottom-left
        // (Layout anchors its box by CornerLabelBottomMarginDip).
        _corner.FontSize = SubPixelProbeWindow.AnswerLabelFontSizeDip;
        _corner.FontWeight = Tokens.FontWeightStrong;
        _corner.Foreground = Tokens.BrushTextPrimary;
        Add(_corner, 24, 0);

        _trialLabel.FontSize = Tokens.FontSizeBody;
        _trialLabel.Foreground = Tokens.BrushTextDim;
        Add(_trialLabel, 24, 0);

        // SP-4d L1/L2: light fills (the Accent fill put the label at 1.08:1 under Midnight — SP-7
        // review 01 O1) and the End test's danger identity as a border, not a fill. "Later" keeps
        // SurfaceMedium: TextPrimary on it is 10.33–18.42:1 and the cue 10.22–14.69:1, above both
        // floors, so the L1 "if it doesn't pass, use SurfaceLight too" clause does not apply.
        // Fixup 01 G2: the label sizes — 96 DIP digits for the answers, the type ramp's two largest
        // steps for the word labels (Display 34 on the 420 × 170 screen buttons, Title 24 on End
        // test's 280 × 120, where every longer label must still clear the border and the cue).
        _answerOne = BuildButton(T("overlays.subpixel.answer_one"), AnswerWidth, AnswerHeight, Tokens.BrushSurfaceLight, ProbeLabelSize.AnswerDigits, out _answerOneCue);
        _answerTwo = BuildButton(T("overlays.subpixel.answer_two"), AnswerWidth, AnswerHeight, Tokens.BrushSurfaceLight, ProbeLabelSize.AnswerDigits, out _answerTwoCue);
        _endTest = BuildButton(T("overlays.subpixel.end_test"), EndButtonWidth, EndButtonHeight, Tokens.BrushSurfaceLight, ProbeLabelSize.EndTest, out _endTestCue);
        _endTest.BorderBrush = Tokens.BrushAccentDanger;
        _endTest.BorderThickness = new Thickness(EndButtonDangerBorderDip);
        Add(_answerOne, 0, 0);
        Add(_answerTwo, 0, 0);
        Add(_endTest, 0, 0);

        _introStart = BuildButton(T("overlays.subpixel.start"), ScreenButtonWidth, ScreenButtonHeight, Tokens.BrushSurfaceLight, ProbeLabelSize.ScreenButton, out _introStartCue);
        _askYes = BuildButton(T("overlays.subpixel.ask_yes"), ScreenButtonWidth, ScreenButtonHeight, Tokens.BrushSurfaceLight, ProbeLabelSize.ScreenButton, out _askYesCue);
        _askLater = BuildButton(T("overlays.subpixel.ask_later"), ScreenButtonWidth, ScreenButtonHeight, Tokens.BrushSurfaceMedium, ProbeLabelSize.ScreenButton, out _askLaterCue);
        _resultDone = BuildButton(T("overlays.subpixel.done"), ScreenButtonWidth, ScreenButtonHeight, Tokens.BrushSurfaceLight, ProbeLabelSize.ScreenButton, out _resultDoneCue);

        // Fixup 01 F1: the Intro text block and the Start button are separate canvas children — the
        // text sits above the demo band and Start below it, so no text is under Start's hit area.
        _introStart.Margin = new Thickness(0);
        Add(_introStart, 0, 0);

        _introPanel = BuildScreen(
            T("overlays.subpixel.intro_title"), T("overlays.subpixel.intro_body"));
        _voidHoldPanel = BuildScreen(
            T("overlays.subpixel.void_hold_title"), T("overlays.subpixel.void_hold_body"));
        _askPanel = BuildAskScreen();
        _resultPanel = BuildScreen(T("overlays.subpixel.result_title"), null, _resultDone);
        _resultDone.Margin = new Thickness(0, TextToButtonGapDip, 0, 0);
        _resultPanel.Children.Insert(1, _resultText);
        _resultText.FontSize = Tokens.FontSizeBodyLarge;
        _resultText.Foreground = Tokens.BrushTextPrimary;
        _resultText.TextWrapping = TextWrapping.Wrap;
        _resultText.TextAlignment = TextAlignment.Center;
        _resultText.MaxWidth = 720;

        RegisterTargets();

        _vm.PropertyChanged += OnVmPropertyChanged;
        SizeChanged += OnSizeChanged;
        CompositionTarget.Rendering += OnCompositionRendering;
        _renderingAttached = true;

        ApplyPhase();
    }

    /// <inheritdoc/>
    public bool ClosedObserved => _closedObserved;

    /// <inheritdoc/>
    public bool IsShown => IsVisible;

    /// <inheritdoc/>
    public void UpdateGaze(PhysicalPoint positionPhys) => _lastGaze = positionPhys;

    private void OnCompositionRendering(object? sender, EventArgs e) => OnFrame();

    /// <summary>
    /// The size-change path, contained like the frame and phase paths (fixup 05 F5): a resize or a
    /// DPI change must not let a layout fault escape to <c>DispatcherUnhandledException</c>. The
    /// arguments are unused; the test drives this handler directly, because a never-shown window's
    /// HWND raises no <c>SizeChanged</c> headlessly.
    /// </summary>
    internal void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            InjectFaultForTest(FaultSiteForTest.Layout);
            Layout();
        }
        catch (Exception ex)
        {
            HandleViewFault(ex);
        }
    }

    /// <summary>Test seam: a fault thrown at an injected fault site (fixup 04 HR1; fixup 05 F1/F5).</summary>
    internal Action? FaultInjectorForTest { get; set; }

    /// <summary>
    /// Test seam: which entry points may fire <see cref="FaultInjectorForTest"/>. Tests arm the
    /// injector after <c>EnsureHandle</c> and select one site, so a real <c>CompositionTarget.Rendering</c>
    /// frame during HWND creation cannot fault the window first (fixup 05 F1).
    /// </summary>
    internal FaultSiteForTest FaultSitesForTest { get; set; }

    /// <summary>The dispatcher entry points the test fault injector may fault (fixup 05 F1/F5).</summary>
    [Flags]
    internal enum FaultSiteForTest
    {
        /// <summary>No entry point.</summary>
        None = 0,

        /// <summary>The per-frame draw path.</summary>
        Frame = 1,

        /// <summary>The phase-change path.</summary>
        Phase = 2,

        /// <summary>The size-change path.</summary>
        Layout = 4,
    }

    /// <summary>Fires the injected fault when the test armed the given site (fixup 05 F1).</summary>
    private void InjectFaultForTest(FaultSiteForTest site)
    {
        if ((FaultSitesForTest & site) != 0) FaultInjectorForTest?.Invoke();
    }

    /// <summary>Test seam: whether the frame hook is still subscribed.</summary>
    internal bool IsFrameHookAttachedForTest => _renderingAttached;

    /// <summary>
    /// One presented frame: advances the ring and the live target dot, and reports the frame gap so
    /// C5 can void a trial whose interval had a gap above 50 ms (test design, trial anatomy).
    /// The frame callback is internal so tests can drive it without a rendered window.
    /// <para>
    /// A fault is contained (fixup 04 HR1): the owner runs without supervision, so an exception
    /// escaping every frame would exhaust the crash policy's fault budget and kill the process. The
    /// fault is logged, the frame hook is detached, drawing stops, and C7 is asked to end the run
    /// exactly once. Nothing is ever rethrown out of here.
    /// </para>
    /// </summary>
    internal void OnFrame()
    {
        if (_faulted) return;

        try
        {
            InjectFaultForTest(FaultSiteForTest.Frame);

            // Stopwatch ticks, not Environment.TickCount64: a 15.6 ms tick compared against a 50 ms
            // threshold reads a three-frame stall as voided or not depending on tick phase (fixup 02 M4).
            long now = _frameTicks();
            if (_vm.Phase is SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2)
            {
                if (_lastFrameTicks != 0)
                    _vm.ReportRenderGap((long)((now - _lastFrameTicks) / _frameTicksPerMs));
                _lastFrameTicks = now;
            }
            else
            {
                _lastFrameTicks = 0;
            }

            DrawPresentation();
            UpdateDwellCues();
        }
        catch (Exception ex)
        {
            HandleViewFault(ex);
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_faulted) return;
        if (e.PropertyName != nameof(SubPixelProbeViewModel.Phase)) return;

        try
        {
            InjectFaultForTest(FaultSiteForTest.Phase);
            ApplyPhase();
        }
        catch (Exception ex)
        {
            HandleViewFault(ex);
        }
    }

    /// <summary>
    /// The frame/phase fault path (HR1): log, detach the frame hook, stop drawing, and ask C7 to end
    /// the run once — posted by C7, never called re-entrantly from here.
    /// </summary>
    private void HandleViewFault(Exception ex)
    {
        Log.Error(ex, "The sub-pixel probe's view callback threw; ending the run");
        DetachRendering();

        if (_faulted) return;
        _faulted = true;
        _viewFault();
    }

    private void DetachRendering()
    {
        if (!_renderingAttached) return;
        _renderingAttached = false;
        CompositionTarget.Rendering -= OnCompositionRendering;
    }

    protected override void OnClosed(EventArgs e)
    {
        // "Already closed" means the event fired, not that Close() was called (review 05 F4).
        _closedObserved = true;
        DetachRendering();
        _vm.PropertyChanged -= OnVmPropertyChanged;
        base.OnClosed(e);

        // The window's own close is a way out, so it ends the captured session.
        _sessionEnded();
    }

    private void Add(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        _scene.Children.Add(element);
    }

    /// <summary>
    /// Builds one probe button and attaches the app's rectangular dwell cue to it (SP-4c D1):
    /// <see cref="DwellOutlineOverlay.Attach"/> wraps the button's content and
    /// <see cref="UpdateDwellCues"/> drives the outline from C5's dwell progress. This is the
    /// keyboard keys'/emoji tabs'/prediction bar's outline, not the toolbar's circular arc: the
    /// probe's buttons are rectangles, and the outline is the standard this app already uses where
    /// a fill cannot carry the feedback (and where the target is not a disc).
    ///
    /// <para>
    /// Fixup 01 G1/G2: the label is centred on both axes (without <c>VerticalAlignment</c> a
    /// stretched TextBlock draws its line at the button's top edge — review 01 F1), and its size
    /// follows the G2 table. The size is chosen inline as a ternary: the geometry guard
    /// (<c>RawGeometryLiteralTests</c>) reads a ternary whole but flags a <c>switch</c> expression,
    /// and a bare size parameter would be a non-token value — so every arm names a real size, the
    /// answer digits' public static double or a theme token.
    /// </para>
    /// </summary>
    private static Border BuildButton(
        string label, double width, double height, Brush background, ProbeLabelSize size,
        out DwellOutlineOverlay cue)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = size == ProbeLabelSize.AnswerDigits ? SubPixelProbeWindow.AnswerLabelFontSizeDip
                : size == ProbeLabelSize.EndTest ? Tokens.FontSizeTitle
                : Tokens.FontSizeDisplay,
            FontWeight = Tokens.FontWeightStrong,
            Foreground = Tokens.BrushTextPrimary,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Tokens.CornerRadiusStandard),
            Background = background,
        };
        cue = DwellOutlineOverlay.Attach(button, new Grid { Children = { text } });
        return button;
    }

    private static TextBlock TitleBlock(string text) => new()
    {
        Text = text,
        FontSize = Tokens.FontSizeTitle,
        FontWeight = Tokens.FontWeightStrong,
        Foreground = Tokens.BrushTextPrimary,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock BodyBlock(string text) => new()
    {
        Text = text,
        FontSize = Tokens.FontSizeBodyLarge,
        Foreground = Tokens.BrushTextSecondary,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 24, 0, 0),
    };

    private StackPanel BuildScreen(string title, string? body, params Border[] buttons)
    {
        var panel = new StackPanel { Width = 720 };
        panel.Children.Add(TitleBlock(title));

        if (body is not null)
            panel.Children.Add(BodyBlock(body));

        foreach (var button in buttons)
        {
            button.Margin = new Thickness(0, 24, 0, 0);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(button);
        }

        panel.Visibility = Visibility.Collapsed;
        _scene.Children.Add(panel);
        return panel;
    }

    /// <summary>
    /// Builds the Ask screen. Unlike the other screens its two buttons sit SIDE BY SIDE: D3's
    /// grown hit areas (516 DIP wide each) must not overlap, and stacked at the other screens'
    /// spacing they would. The 120 DIP gap puts them 24 DIP apart edge to edge; the 96 DIP
    /// text-to-button gap keeps the title clear of both grown areas (fixup 01 F1). The screen's
    /// exits, its 60 s timeout and its phase transitions are untouched.
    /// </summary>
    private StackPanel BuildAskScreen()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _askYes.Margin = new Thickness(0, TextToButtonGapDip, AskButtonGapDip / 2, 0);
        _askLater.Margin = new Thickness(AskButtonGapDip / 2, TextToButtonGapDip, 0, 0);
        row.Children.Add(_askYes);
        row.Children.Add(_askLater);

        var panel = new StackPanel { Width = AskPanelWidth };
        panel.Children.Add(TitleBlock(T("overlays.subpixel.ask_title")));
        panel.Children.Add(row);
        panel.Visibility = Visibility.Collapsed;
        _scene.Children.Add(panel);
        return panel;
    }

    private void RegisterTargets()
    {
        Register(SubPixelProbeViewModel.AnswerOneTargetId, AnswerWidth, AnswerHeight, ButtonHitMarginDip);
        Register(SubPixelProbeViewModel.AnswerTwoTargetId, AnswerWidth, AnswerHeight, ButtonHitMarginDip);
        Register(SubPixelProbeViewModel.EndTestTargetId, EndButtonWidth, EndButtonHeight, EndButtonHitMarginDip);
        Register(SubPixelProbeViewModel.IntroStartTargetId, ScreenButtonWidth, ScreenButtonHeight, ButtonHitMarginDip);
        Register(SubPixelProbeViewModel.AskYesTargetId, ScreenButtonWidth, ScreenButtonHeight, ButtonHitMarginDip);
        Register(SubPixelProbeViewModel.AskLaterTargetId, ScreenButtonWidth, ScreenButtonHeight, ButtonHitMarginDip);
        Register(SubPixelProbeViewModel.ResultDoneTargetId, ScreenButtonWidth, ScreenButtonHeight, ButtonHitMarginDip);
    }

    /// <summary>
    /// Registers one dwell target with its D3 (amended) hit area: the visible button grown by its
    /// margin on every side, as a rectangle. The provisional rectangle is replaced by the laid-out
    /// one in <see cref="Layout"/> before any frame draws.
    /// </summary>
    private void Register(string id, double width, double height, double marginDip)
    {
        var provisional = new PhysicalRect(
            0, 0, (width + 2 * marginDip) * _scale, (height + 2 * marginDip) * _scale);
        _vm.RegisterRect(id, provisional, dwellMs: 0, onActivated: static () => { });
    }

    /// <summary>The D3 growth of a target's hit rectangle, in DIP per side.</summary>
    private static double HitMarginFor(string id) =>
        id == SubPixelProbeViewModel.EndTestTargetId ? EndButtonHitMarginDip : ButtonHitMarginDip;

    /// <summary>
    /// Lays the screens and buttons out for the current window size, then re-centres every dwell
    /// target from its laid-out position. Runs on every size change and on every phase change
    /// (<see cref="ApplyPhase"/>), because a panel that is Collapsed until its phase starts has no
    /// arrange until then.
    /// </summary>
    internal void Layout()
    {
        double width = Width;
        double height = Height;
        if (width <= 0 || height <= 0) return;

        Canvas.SetLeft(_question, 0);
        Canvas.SetTop(_question, height * 0.22);

        Canvas.SetLeft(_answerOne, width * 0.22 - AnswerWidth / 2);
        Canvas.SetTop(_answerOne, height * 0.52 - AnswerHeight / 2);
        Canvas.SetLeft(_answerTwo, width * 0.78 - AnswerWidth / 2);
        Canvas.SetTop(_answerTwo, height * 0.52 - AnswerHeight / 2);

        Canvas.SetLeft(_endTest, width - EndButtonWidth - 24);
        Canvas.SetTop(_endTest, height - EndButtonHeight - 24);

        // SP-4e T2: the corner digit's box is anchored by its bottom edge
        // (CornerLabelBottomMarginDip, the same 24 DIP End test keeps from the bottom) and the
        // trial label stacks above it by a fixed gap — at 96 DIP the old fixed top (height - 62)
        // would clip the digit at the window's bottom edge and draw it through the label's line.
        _corner.Measure(new Size(width, height));
        double cornerTop = height - CornerLabelBottomMarginDip - _corner.DesiredSize.Height;
        Canvas.SetTop(_corner, cornerTop);

        _trialLabel.Measure(new Size(width, height));
        Canvas.SetTop(_trialLabel, cornerTop - CornerToTrialLabelGapDip - _trialLabel.DesiredSize.Height);

        // D5 + fixup 01 F1 + fixup 02 R1: the demo band is the ring's path through the screen
        // centre. The Intro text sits ABOVE it and the Start button BELOW it, 128 DIP clear of the
        // band's bottom edge — 80 DIP clear of Start's grown hit area, more than the owner's worst
        // gaze bias. The clamp keeps that whole hit area on-screen for a large ring.
        _introPanel.Measure(new Size(width, height));
        Canvas.SetLeft(_introPanel, (width - _introPanel.DesiredSize.Width) / 2);
        Canvas.SetTop(_introPanel,
            height / 2 - _ring.SizeDip / 2 - IntroTextGapDip - _introPanel.DesiredSize.Height);

        double maxStartTop = height - ScreenButtonHeight - ButtonHitMarginDip - IntroBottomMarginDip;
        Canvas.SetLeft(_introStart, (width - ScreenButtonWidth) / 2);
        Canvas.SetTop(_introStart, Math.Min(
            height / 2 + _ring.SizeDip / 2 + IntroStartGapDip,
            maxStartTop));

        Centre(_voidHoldPanel, width, height);
        Centre(_askPanel, width, height);
        Centre(_resultPanel, width, height);

        // The demo's step law re-anchored onto the screen centre: the ring starts half its travel
        // left of centre so the whole path is centred, and drifts East (D5's "same drift path" is
        // the trial law's; the direction is the demo's own, chosen so the path is predictable).
        _demoMotion = new SubPixelAbDriftMotion(
            new PhysicalPoint(
                width * _scale / 2.0 - DemoTravelPx / 2.0,
                height * _scale / 2.0),
            SubPixelAbDirection.East);

        EnsureArranged();
        UpdateTargetGeometry();
    }

    private void Centre(FrameworkElement panel, double width, double height)
    {
        panel.Measure(new Size(width, height));
        Canvas.SetLeft(panel, (width - panel.DesiredSize.Width) / 2);
        Canvas.SetTop(panel, (height - panel.DesiredSize.Height) / 2);
    }

    /// <summary>
    /// Recomputes every target's registered centre from the element's <b>laid-out</b> position in
    /// the scene, in physical pixels for the injected scale. The screen buttons are children of
    /// StackPanels, so their Canvas attachments are NaN — reading Canvas.Left/Top gave them no hit
    /// circle at all (fixup 01 B2). A target whose phase is not showing is parked off-screen, so a
    /// hidden button can never hold a live hit circle (fixup 03 N2).
    /// </summary>
    internal void UpdateTargetGeometry()
    {
        UpdateTargetGeometry(SubPixelProbeViewModel.AnswerOneTargetId, _answerOne);
        UpdateTargetGeometry(SubPixelProbeViewModel.AnswerTwoTargetId, _answerTwo);
        UpdateTargetGeometry(SubPixelProbeViewModel.EndTestTargetId, _endTest);
        UpdateTargetGeometry(SubPixelProbeViewModel.IntroStartTargetId, _introStart);
        UpdateTargetGeometry(SubPixelProbeViewModel.AskYesTargetId, _askYes);
        UpdateTargetGeometry(SubPixelProbeViewModel.AskLaterTargetId, _askLater);
        UpdateTargetGeometry(SubPixelProbeViewModel.ResultDoneTargetId, _resultDone);
    }

    private static readonly PhysicalPoint OffScreenCentre = new(-10_000, -10_000);

    /// <summary>
    /// A rectangle no gaze sample can reach. A target whose button is not on screen keeps its entry
    /// in the detector (the registrar has no phase concept), so it must be parked here instead of
    /// holding a stale hit area — otherwise a gaze at the screen's top-left corner dwells on an
    /// invisible button (fixup 01 B2).
    /// </summary>
    private static readonly PhysicalRect OffScreenRect =
        new(OffScreenCentre.X, OffScreenCentre.Y, 0, 0);

    private void UpdateTargetGeometry(string id, FrameworkElement element)
    {
        // Shown means the button AND every ancestor below _scene is visible: the screen buttons
        // never change their own Visibility — their StackPanel collapses instead, and a child of a
        // collapsed panel keeps its last arranged size and transform. Checking only the button left
        // hidden buttons dwell-live at their old areas (fixup 03 N2).
        if (!IsShownInScene(element) || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            _vm.UpdateBounds(id, OffScreenRect);
            return;
        }

        // The D3 (amended) hit area: the laid-out button grown by its margin on every side, in
        // physical pixels at the injected scale. Re-published on every layout, state-preserving.
        double margin = HitMarginFor(id);
        Point topLeft = element.TransformToAncestor(_scene).Transform(new Point(0, 0));
        _vm.UpdateBounds(id, new PhysicalRect(
            (topLeft.X - margin) * _scale,
            (topLeft.Y - margin) * _scale,
            (element.ActualWidth + 2 * margin) * _scale,
            (element.ActualHeight + 2 * margin) * _scale));
    }

    /// <summary>Whether <paramref name="element"/> and all its ancestors below <see cref="_scene"/> are Visible.</summary>
    private bool IsShownInScene(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, _scene)) return true;
            if (node is UIElement ui && ui.Visibility != Visibility.Visible) return false;
        }

        return false;   // detached from the scene
    }

    /// <summary>
    /// Forces a layout pass when the scene has none yet (the pre-Show construction and headless
    /// tests): Measure/Arrange at the window's size, then flush the visibility invalidations the
    /// phase change just made. A shown window is already laid out, so this is effectively a no-op.
    /// </summary>
    private void EnsureArranged()
    {
        if (_scene.ActualWidth <= 0 || _scene.ActualHeight <= 0)
        {
            var size = new Size(Width, Height);
            if (size.Width <= 0 || size.Height <= 0) return;
            _scene.Measure(size);
            _scene.Arrange(new Rect(size));
        }

        _scene.UpdateLayout();
    }

    /// <summary>Test seam: the button element a dwell target is registered against (W6).</summary>
    internal FrameworkElement? TargetElementForTest(string id) => id switch
    {
        SubPixelProbeViewModel.AnswerOneTargetId => _answerOne,
        SubPixelProbeViewModel.AnswerTwoTargetId => _answerTwo,
        SubPixelProbeViewModel.EndTestTargetId => _endTest,
        SubPixelProbeViewModel.IntroStartTargetId => _introStart,
        SubPixelProbeViewModel.AskYesTargetId => _askYes,
        SubPixelProbeViewModel.AskLaterTargetId => _askLater,
        SubPixelProbeViewModel.ResultDoneTargetId => _resultDone,
        _ => null,
    };

    /// <summary>Test seam (M5): whether the probe ring is currently drawn.</summary>
    internal bool IsRingDrawnForTest => _ring.Visibility == Visibility.Visible;

    /// <summary>Test seam (SP-4c D1): the dwell cue outline a button draws.</summary>
    internal DwellOutlineOverlay? CueForTest(string id) => id switch
    {
        SubPixelProbeViewModel.AnswerOneTargetId => _answerOneCue,
        SubPixelProbeViewModel.AnswerTwoTargetId => _answerTwoCue,
        SubPixelProbeViewModel.EndTestTargetId => _endTestCue,
        SubPixelProbeViewModel.IntroStartTargetId => _introStartCue,
        SubPixelProbeViewModel.AskYesTargetId => _askYesCue,
        SubPixelProbeViewModel.AskLaterTargetId => _askLaterCue,
        SubPixelProbeViewModel.ResultDoneTargetId => _resultDoneCue,
        _ => null,
    };

    /// <summary>Test seam (SP-4c D5): the ring the demo and the trials draw with.</summary>
    internal ProbeRingVisual RingForTest => _ring;

    /// <summary>Test seam (SP-4e T2): the interval corner digit's TextBlock ("1"/"2").</summary>
    internal TextBlock CornerDigitForTest => _corner;

    /// <summary>Test seam (SP-4c D2): a screen's panel, for the layout-fit assertions.</summary>
    internal FrameworkElement? PanelForTest(SubPixelProbePhase phase) => phase switch
    {
        SubPixelProbePhase.Intro => _introPanel,
        SubPixelProbePhase.VoidHold => _voidHoldPanel,
        SubPixelProbePhase.Ask => _askPanel,
        SubPixelProbePhase.Result => _resultPanel,
        _ => null,
    };

    /// <summary>Test seam (M5): the VoidHold panel, for the "rest your eyes" visibility assertion.</summary>
    internal FrameworkElement VoidHoldPanelForTest => _voidHoldPanel;

    /// <summary>Test seam (M5): the Result screen's rendered verdict text.</summary>
    internal string ResultTextForTest => _resultText.Text;

    private void ApplyPhase()
    {
        bool interval = _vm.Phase is SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2;
        bool ended = _vm.Phase == SubPixelProbePhase.Ended;

        SetVisible(_introPanel, _vm.Phase == SubPixelProbePhase.Intro);
        SetVisible(_introStart, _vm.Phase == SubPixelProbePhase.Intro);
        SetVisible(_voidHoldPanel, _vm.Phase == SubPixelProbePhase.VoidHold);
        SetVisible(_askPanel, _vm.Phase == SubPixelProbePhase.Ask);
        SetVisible(_resultPanel, _vm.Phase == SubPixelProbePhase.Result);

        SetVisible(_answerOne, _vm.Phase == SubPixelProbePhase.Answer);
        SetVisible(_answerTwo, _vm.Phase == SubPixelProbePhase.Answer);
        SetVisible(_endTest, !ended && _vm.Phase != SubPixelProbePhase.Idle);

        _ring.Visibility = interval || _vm.Phase == SubPixelProbePhase.Intro
            ? Visibility.Visible
            : Visibility.Collapsed;
        _targetDot.Visibility = Visibility.Collapsed;

        // D5: the demo repeats its interval pair; entering the Intro restarts the loop. The anchor
        // itself is taken lazily on the first demo frame, so a frame clock that starts at 0 works.
        if (_vm.Phase == SubPixelProbePhase.Intro) _demoAnchored = false;

        _corner.Text = _vm.Phase switch
        {
            SubPixelProbePhase.Interval1 => T("overlays.subpixel.label_one"),
            SubPixelProbePhase.Interval2 => T("overlays.subpixel.label_two"),
            _ => string.Empty,
        };

        _question.Text = _vm.Phase == SubPixelProbePhase.Answer
            ? T(_vm.CurrentBlock == SubPixelAbBlock.Static
                ? "overlays.subpixel.static_question"
                : "overlays.subpixel.motion_question")
            : string.Empty;

        _trialLabel.Text = TrialLabel();
        if (_vm.Phase == SubPixelProbePhase.Result) _resultText.Text = ResultText();

        // The look follows the block: quiet for the moving blocks, pinned for the still one, the
        // way production's ring is only still when pinned (test design, Ring look).
        _ring.Opacity = _vm.CurrentBlock == SubPixelAbBlock.Static
            ? ProbeRingVisual.PinnedLookOpacity
            : ProbeRingVisual.QuietLookOpacity;

        // The phase decides which panels are visible, so it also decides which targets have render
        // geometry: a panel that was Collapsed has no arrange until now (fixup 01 B2).
        Layout();
    }

    private string TrialLabel()
    {
        if (_vm.Phase is not (SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2 or SubPixelProbePhase.Gap))
            return string.Empty;
        if (_vm.IsDemo) return T("overlays.subpixel.demo_hint");
        if (_vm.CurrentTrial is not { } trial) return string.Empty;

        string block = T(trial.Block switch
        {
            SubPixelAbBlock.Drift => "overlays.subpixel.block_drift",
            SubPixelAbBlock.Static => "overlays.subpixel.block_static",
            _ => "overlays.subpixel.block_live",
        });
        return T("overlays.subpixel.trial_label", block, trial.IndexInBlock + 1);
    }

    private string ResultText()
    {
        var verdict = _vm.Session2Verdict?.Outcome ?? _vm.Session1Verdict?.Outcome;
        string line = verdict switch
        {
            SubPixelAbOutcome.Convicts => T("overlays.subpixel.result_convicted"),
            SubPixelAbOutcome.Acquits => T("overlays.subpixel.result_acquitted"),
            _ => T("overlays.subpixel.result_incomplete"),
        };

        if (_vm.Session1Verdict is { StaticReadout: var readout }
            && _vm.Session2Verdict is null)
        {
            string staticLine = readout switch
            {
                SubPixelAbStaticReadout.CanTellStillRingsApart => T("overlays.subpixel.static_can_tell"),
                SubPixelAbStaticReadout.CannotTellStillRingsApart => T("overlays.subpixel.static_cannot_tell"),
                _ => T("overlays.subpixel.static_incomplete"),
            };
            line = line + "\n" + staticLine;
        }

        return line;
    }

    private void DrawPresentation()
    {
        if (_vm.Phase == SubPixelProbePhase.Intro)
        {
            DrawIntroDemo();
            return;
        }

        if (_vm.Phase is not (SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2)) return;
        if (_vm.CurrentMotion is not { } motion) return;

        var condition = _vm.Phase == SubPixelProbePhase.Interval1
            ? _vm.Interval1Condition
            : _vm.Interval2Condition;
        bool snap = condition == SubPixelAbCondition.A;
        long elapsed = _vm.PhaseElapsedMs;

        switch (motion)
        {
            case SubPixelAbDriftMotion drift:
                _ring.Place(drift.PositionAt(elapsed), snap);
                break;

            case SubPixelAbStaticMotion park:
                _ring.PlaceStaticPark(park.Position, snap);
                break;

            case SubPixelAbLiveMotion live:
                // The ring follows the owner's filtered gaze; the dot is the guide to follow.
                _ring.Place(_lastGaze ?? live.TargetPositionAt(elapsed), snap);

                // The injected scale, like everything else in C6 — not the ambient DpiHelper
                // (fixup 04 HR9).
                var target = live.TargetPositionAt(elapsed);
                Canvas.SetLeft(_targetDot, target.X / _scale - DotDiameter / 2);
                Canvas.SetTop(_targetDot, target.Y / _scale - DotDiameter / 2);
                _targetDot.Visibility = Visibility.Visible;
                break;
        }
    }

    /// <summary>
    /// D5 — the Intro screen's motion demo: the probe's real ring (same visual, same quiet look,
    /// same size) drifts through the screen centre at a trial's drift speed, looping one scripted
    /// interval pair unlabelled — the pair's first condition for one interval, the 0.8 s gap with
    /// the ring hidden, then the other from the same start, exactly the way a trial's two intervals
    /// look. The condition order ALTERNATES per loop (fixup 01 F5: A→B, then B→A), so the demo
    /// teaches no positional habit the trials do not have. Driven by the injected frame clock, so
    /// it is deterministic headless; every other phase draws nothing here.
    /// </summary>
    private void DrawIntroDemo()
    {
        long now = _frameTicks();
        if (!_demoAnchored)
        {
            _demoAnchored = true;
            _demoAnchorTicks = now;
        }

        if (now < _demoAnchorTicks) _demoAnchorTicks = now;   // a reset frame clock re-anchors
        long elapsedMs = (long)((now - _demoAnchorTicks) / _frameTicksPerMs);
        long cycle = elapsedMs / DemoCycleMs;
        bool bFirst = (cycle & 1) == 1;
        long at = elapsedMs % DemoCycleMs;

        // One interval pair: first condition, gap, second condition, gap — the pair's second gap
        // exists only so the loop re-enters the pair the way a block's next presentation does
        // (ring hidden at the seam).
        if (at >= DemoIntervalMs && at < DemoIntervalMs + DemoGapMs)
        {
            _ring.Visibility = Visibility.Collapsed;
            return;
        }

        if (at >= 2L * DemoIntervalMs + DemoGapMs)
        {
            _ring.Visibility = Visibility.Collapsed;
            return;
        }

        bool snap;
        long intervalMs;
        if (at < DemoIntervalMs)
        {
            snap = !bFirst;                                 // the pair's first condition
            intervalMs = at;
        }
        else
        {
            snap = bFirst;                                  // the pair's second condition
            intervalMs = at - DemoIntervalMs - DemoGapMs;
        }

        _ring.Visibility = Visibility.Visible;
        _ring.Place(_demoMotion.PositionAt(intervalMs), snap);
    }

    /// <summary>
    /// D1: pushes every button's dwell progress into its outline. Runs once per presented frame
    /// (like the rest of the drawing), allocation-free — <see cref="DwellOutlineOverlay.SetProgress"/>
    /// only writes two stroke thicknesses. A button that is not on screen has no live dwell state,
    /// so its progress is 0 and its outline collapses.
    /// </summary>
    private void UpdateDwellCues()
    {
        _answerOneCue.SetProgress(_vm.GetProgress(SubPixelProbeViewModel.AnswerOneTargetId, DwellCategory.Control));
        _answerTwoCue.SetProgress(_vm.GetProgress(SubPixelProbeViewModel.AnswerTwoTargetId, DwellCategory.Control));
        _endTestCue.SetProgress(_vm.GetProgress(SubPixelProbeViewModel.EndTestTargetId, DwellCategory.Control));
        _introStartCue.SetProgress(_vm.GetProgress(SubPixelProbeViewModel.IntroStartTargetId, DwellCategory.Control));
        _askYesCue.SetProgress(_vm.GetProgress(SubPixelProbeViewModel.AskYesTargetId, DwellCategory.Control));
        _askLaterCue.SetProgress(_vm.GetProgress(SubPixelProbeViewModel.AskLaterTargetId, DwellCategory.Control));
        _resultDoneCue.SetProgress(_vm.GetProgress(SubPixelProbeViewModel.ResultDoneTargetId, DwellCategory.Control));
    }

    private static void SetVisible(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static string T(string key) => StringTable.Current.T(key);

    private static string T(string key, params object?[] args) => StringTable.Current.T(key, args);
}
