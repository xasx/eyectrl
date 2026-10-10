namespace EyeCtrl.App.Diagnostics.SubPixel;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
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
/// current presentation) and registers C5's seven dwell targets with their physical hit circles.
/// Its close path invokes the session-end callback captured at construction — never a coordinator
/// field — and sets <see cref="ClosedObserved"/> before it does, so a re-entrant close is a no-op
/// and "already closed" means the event fired (review 05 F3/F4).
/// </para>
///
/// <para>
/// Nothing here sets a snap or layout-rounding flag on the probe's ancestors (SP-2 review 01): the
/// backdrop is drawn 1:1 because the image is exactly screen-DIPs wide at the injected scale, and
/// the ring's placement is the only rounding in the subtree.
/// </para>
/// </summary>
internal sealed class SubPixelProbeWindow : OverlayWindow, IProbeWindow
{
    private const double AnswerWidth = 220;
    private const double AnswerHeight = 140;
    private const double ScreenButtonWidth = 260;
    private const double ScreenButtonHeight = 110;
    private const double EndButtonWidth = 170;
    private const double EndButtonHeight = 70;
    private const double DotDiameter = 14;

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

    private PhysicalPoint? _lastGaze;
    private long _lastFrameTicks;
    private readonly Action _viewFault;
    private bool _faulted;
    private bool _renderingAttached;
    private readonly Func<long> _frameTicks;
    private readonly double _frameTicksPerMs;
    private bool _closedObserved;

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

        _corner.FontSize = Tokens.FontSizeBodyLarge;
        _corner.FontWeight = Tokens.FontWeightStrong;
        _corner.Foreground = Tokens.BrushTextSecondary;
        Add(_corner, 24, 0);

        _trialLabel.FontSize = Tokens.FontSizeBody;
        _trialLabel.Foreground = Tokens.BrushTextDim;
        Add(_trialLabel, 24, 0);

        _answerOne = BuildButton(T("overlays.subpixel.answer_one"), AnswerWidth, AnswerHeight, Tokens.BrushAccent);
        _answerTwo = BuildButton(T("overlays.subpixel.answer_two"), AnswerWidth, AnswerHeight, Tokens.BrushAccent);
        _endTest = BuildButton(T("overlays.subpixel.end_test"), EndButtonWidth, EndButtonHeight, Tokens.BrushAccentDanger);
        Add(_answerOne, 0, 0);
        Add(_answerTwo, 0, 0);
        Add(_endTest, 0, 0);

        _introStart = BuildButton(T("overlays.subpixel.start"), ScreenButtonWidth, ScreenButtonHeight, Tokens.BrushAccent);
        _askYes = BuildButton(T("overlays.subpixel.ask_yes"), ScreenButtonWidth, ScreenButtonHeight, Tokens.BrushAccent);
        _askLater = BuildButton(T("overlays.subpixel.ask_later"), ScreenButtonWidth, ScreenButtonHeight, Tokens.BrushSurfaceMedium);
        _resultDone = BuildButton(T("overlays.subpixel.done"), ScreenButtonWidth, ScreenButtonHeight, Tokens.BrushAccent);

        _introPanel = BuildScreen(
            T("overlays.subpixel.intro_title"), T("overlays.subpixel.intro_body"), _introStart);
        _voidHoldPanel = BuildScreen(
            T("overlays.subpixel.void_hold_title"), T("overlays.subpixel.void_hold_body"));
        _askPanel = BuildScreen(
            T("overlays.subpixel.ask_title"), null, _askYes, _askLater);
        _resultPanel = BuildScreen(T("overlays.subpixel.result_title"), null, _resultDone);
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

    private Border BuildButton(string label, double width, double height, Brush background)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = Tokens.FontSizeBodyLarge,
            FontWeight = Tokens.FontWeightStrong,
            Foreground = Tokens.BrushTextPrimary,
            TextAlignment = TextAlignment.Center,
        };

        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Tokens.CornerRadiusStandard),
            Background = background,
            Child = new Grid { Children = { text } },
        };
    }

    private StackPanel BuildScreen(string title, string? body, params Border[] buttons)
    {
        var panel = new StackPanel { Width = 720 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = Tokens.FontSizeTitle,
            FontWeight = Tokens.FontWeightStrong,
            Foreground = Tokens.BrushTextPrimary,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        if (body is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = Tokens.FontSizeBodyLarge,
                Foreground = Tokens.BrushTextSecondary,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 24, 0, 0),
            });
        }

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

    private void RegisterTargets()
    {
        Register(SubPixelProbeViewModel.AnswerOneTargetId, AnswerWidth, AnswerHeight);
        Register(SubPixelProbeViewModel.AnswerTwoTargetId, AnswerWidth, AnswerHeight);
        Register(SubPixelProbeViewModel.EndTestTargetId, EndButtonWidth, EndButtonHeight);
        Register(SubPixelProbeViewModel.IntroStartTargetId, ScreenButtonWidth, ScreenButtonHeight);
        Register(SubPixelProbeViewModel.AskYesTargetId, ScreenButtonWidth, ScreenButtonHeight);
        Register(SubPixelProbeViewModel.AskLaterTargetId, ScreenButtonWidth, ScreenButtonHeight);
        Register(SubPixelProbeViewModel.ResultDoneTargetId, ScreenButtonWidth, ScreenButtonHeight);
    }

    private void Register(string id, double width, double height)
    {
        double radiusPhysical = Math.Min(width, height) * _scale / 2.0;
        _vm.Register(id, DwellCategory.Control, radiusPhysical, dwellMs: 0, onActivated: static () => { });
    }

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

        Canvas.SetTop(_corner, height - 62);
        Canvas.SetTop(_trialLabel, height - 92);

        Centre(_introPanel, width, height);
        Centre(_voidHoldPanel, width, height);
        Centre(_askPanel, width, height);
        Centre(_resultPanel, width, height);

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

    /// <summary>
    /// A centre no gaze sample can reach. A target whose button is not on screen keeps its entry in
    /// the detector (the registrar has no phase concept), so it must be parked here instead of
    /// holding a stale centre — otherwise a gaze at the screen's top-left corner dwells on an
    /// invisible button (fixup 01 B2).
    /// </summary>
    private static readonly PhysicalPoint OffScreenCentre = new(-10_000, -10_000);

    private void UpdateTargetGeometry(string id, FrameworkElement element)
    {
        // Shown means the button AND every ancestor below _scene is visible: the screen buttons
        // never change their own Visibility — their StackPanel collapses instead, and a child of a
        // collapsed panel keeps its last arranged size and transform. Checking only the button left
        // hidden buttons dwell-live at their old centres, stealing up to 48 % of a visible button's
        // disc (fixup 03 N2).
        if (!IsShownInScene(element) || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            _vm.UpdatePosition(id, DwellCategory.Control, OffScreenCentre);
            return;
        }

        Point sceneCentre = element.TransformToAncestor(_scene)
            .Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        _vm.UpdatePosition(id, DwellCategory.Control, new PhysicalPoint(
            sceneCentre.X * _scale, sceneCentre.Y * _scale));
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

    /// <summary>Test seam (M5): the VoidHold panel, for the "rest your eyes" visibility assertion.</summary>
    internal FrameworkElement VoidHoldPanelForTest => _voidHoldPanel;

    /// <summary>Test seam (M5): the Result screen's rendered verdict text.</summary>
    internal string ResultTextForTest => _resultText.Text;

    private void ApplyPhase()
    {
        bool interval = _vm.Phase is SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2;
        bool ended = _vm.Phase == SubPixelProbePhase.Ended;

        SetVisible(_introPanel, _vm.Phase == SubPixelProbePhase.Intro);
        SetVisible(_voidHoldPanel, _vm.Phase == SubPixelProbePhase.VoidHold);
        SetVisible(_askPanel, _vm.Phase == SubPixelProbePhase.Ask);
        SetVisible(_resultPanel, _vm.Phase == SubPixelProbePhase.Result);

        SetVisible(_answerOne, _vm.Phase == SubPixelProbePhase.Answer);
        SetVisible(_answerTwo, _vm.Phase == SubPixelProbePhase.Answer);
        SetVisible(_endTest, !ended && _vm.Phase != SubPixelProbePhase.Idle);

        _ring.Visibility = interval ? Visibility.Visible : Visibility.Collapsed;
        _targetDot.Visibility = Visibility.Collapsed;

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

    private static void SetVisible(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static string T(string key) => StringTable.Current.T(key);

    private static string T(string key, params object?[] args) => StringTable.Current.T(key, args);
}
