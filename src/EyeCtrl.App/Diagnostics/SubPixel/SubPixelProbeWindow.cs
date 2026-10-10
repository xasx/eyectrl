namespace EyeCtrl.App.Diagnostics.SubPixel;

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
    private long _lastFrameMs;
    private bool _closedObserved;

    /// <summary>
    /// Builds the probe window for one run. The window is not shown here; C7 shows it through
    /// <see cref="IProbeWindow.Show"/>.
    /// </summary>
    /// <param name="backdrop">The full-screen capture, or <c>null</c> for a plain backdrop.</param>
    /// <param name="vm">The run's view model; its phase drives every screen.</param>
    /// <param name="cursorSettings">The ring look the probe draws with.</param>
    /// <param name="sessionEnded">Invoked once, when this window's close is observed.</param>
    public SubPixelProbeWindow(
        BitmapSource? backdrop,
        SubPixelProbeViewModel vm,
        CursorSettings cursorSettings,
        Action sessionEnded)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _sessionEnded = sessionEnded ?? throw new ArgumentNullException(nameof(sessionEnded));
        _scale = DpiHelper.ScaleX <= 0 ? 1.0 : DpiHelper.ScaleX;

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
        _backdrop.Width = SystemParameters.PrimaryScreenWidth;
        _backdrop.Height = SystemParameters.PrimaryScreenHeight;
        if (backdrop is not null) _backdrop.Source = backdrop;
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
        Layout();

        _vm.PropertyChanged += OnVmPropertyChanged;
        SizeChanged += (_, _) => Layout();
        CompositionTarget.Rendering += OnCompositionRendering;

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
    /// One presented frame: advances the ring and the live target dot, and reports the frame gap so
    /// C5 can void a trial whose interval had a gap above 50 ms (test design, trial anatomy).
    /// The frame callback is internal so tests can drive it without a rendered window.
    /// </summary>
    internal void OnFrame()
    {
        long now = Environment.TickCount64;
        if (_vm.Phase is SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2)
        {
            if (_lastFrameMs != 0) _vm.ReportRenderGap(now - _lastFrameMs);
            _lastFrameMs = now;
        }
        else
        {
            _lastFrameMs = 0;
        }

        DrawPresentation();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubPixelProbeViewModel.Phase)) ApplyPhase();
    }

    protected override void OnClosed(EventArgs e)
    {
        // "Already closed" means the event fired, not that Close() was called (review 05 F4).
        _closedObserved = true;
        CompositionTarget.Rendering -= OnCompositionRendering;
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
    /// target. Runs on every size change; a headless window that was never shown has no size and
    /// keeps the registration-time positions.
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

        UpdateTargetGeometry(SubPixelProbeViewModel.AnswerOneTargetId, _answerOne, AnswerWidth, AnswerHeight);
        UpdateTargetGeometry(SubPixelProbeViewModel.AnswerTwoTargetId, _answerTwo, AnswerWidth, AnswerHeight);
        UpdateTargetGeometry(SubPixelProbeViewModel.EndTestTargetId, _endTest, EndButtonWidth, EndButtonHeight);
        UpdateTargetGeometry(SubPixelProbeViewModel.IntroStartTargetId, _introStart, ScreenButtonWidth, ScreenButtonHeight);
        UpdateTargetGeometry(SubPixelProbeViewModel.AskYesTargetId, _askYes, ScreenButtonWidth, ScreenButtonHeight);
        UpdateTargetGeometry(SubPixelProbeViewModel.AskLaterTargetId, _askLater, ScreenButtonWidth, ScreenButtonHeight);
        UpdateTargetGeometry(SubPixelProbeViewModel.ResultDoneTargetId, _resultDone, ScreenButtonWidth, ScreenButtonHeight);
    }

    private void Centre(FrameworkElement panel, double width, double height)
    {
        panel.Measure(new Size(width, height));
        Canvas.SetLeft(panel, (width - panel.DesiredSize.Width) / 2);
        Canvas.SetTop(panel, (height - panel.DesiredSize.Height) / 2);
    }

    private void UpdateTargetGeometry(string id, FrameworkElement element, double width, double height)
    {
        double left = Canvas.GetLeft(element);
        double top = Canvas.GetTop(element);
        _vm.UpdatePosition(id, DwellCategory.Control, new PhysicalPoint(
            (left + width / 2) * _scale, (top + height / 2) * _scale));
    }

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
                var target = live.TargetPositionAt(elapsed).ToLogical();
                Canvas.SetLeft(_targetDot, target.X - DotDiameter / 2);
                Canvas.SetTop(_targetDot, target.Y - DotDiameter / 2);
                _targetDot.Visibility = Visibility.Visible;
                break;
        }
    }

    private static void SetVisible(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static string T(string key) => StringTable.Current.T(key);

    private static string T(string key, params object?[] args) => StringTable.Current.T(key, args);
}
