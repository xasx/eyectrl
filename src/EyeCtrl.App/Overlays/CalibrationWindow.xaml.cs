namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

public partial class CalibrationWindow : OverlayWindow, ICalibrationOverlay
{
    private CalibrationViewModel? _viewModel;
    private Storyboard? _pulseAnimation;
    private DispatcherTimer? _autoDismissTimer;

    /// <summary>
    /// How long the active target takes to slide to its next position.
    /// </summary>
    /// <remarks>
    /// Long enough to be seen as movement rather than a jump — the whole point is that motion, not
    /// reading, says where to look — and short enough that the drawn ring trails the enforced band only
    /// briefly. 220 ms is roughly the latency of a voluntary saccade, so the user's eye arrives about
    /// when the target does instead of chasing it.
    /// </remarks>
    private static readonly TimeSpan TargetSlideDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>
    /// One marker per target for the whole session, index-aligned with
    /// <c>CalibrationViewModel.TargetPositions</c>. Built once when the set is published and only
    /// restyled afterwards, so an advance never rebuilds visuals mid-animation.
    /// </summary>
    private readonly List<Ellipse> _targetSetMarkers = new();

    /// <summary>
    /// Bumped by every slide, so a superseded animation's completion handler can recognise itself as
    /// stale and stay out of the way. See <see cref="MoveActiveLayerTo"/>.
    /// </summary>
    private int _slideGeneration;

    public CalibrationWindow()
    {
        InitializeComponent();

        // Retry discards the current (poor) result and starts a fresh session.
        RetryButton.Click += (_, _) =>
        {
            RetryPanel.Visibility = Visibility.Collapsed;
            _viewModel?.Retry();
        };

        // Accept keeps the result and returns the VM to Idle so re-calibration works later.
        AcceptButton.Click += (_, _) => DismissAndReset();

        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        var state = _viewModel?.State ?? CalibrationState.Idle;
        if (state is CalibrationState.Collecting or CalibrationState.Training)
            _viewModel?.Cancel();
        else
            DismissAndReset();
    }

    public void SetViewModel(CalibrationViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CalibrationViewModel.TargetPositions):
                Dispatcher.Invoke(BuildTargetSet);
                break;
            case nameof(CalibrationViewModel.CurrentTargetIndex):
                Dispatcher.Invoke(UpdateTargetSetStates);
                break;
            case nameof(CalibrationViewModel.CurrentTargetPosition):
                // Animated: the move itself is what tells the user where to look next, so it must be
                // seen rather than teleported past. Sizing is applied immediately; only the position
                // slides.
                Dispatcher.Invoke(() => PositionTarget(_viewModel!.CurrentTargetPosition, animate: true));
                break;
            case nameof(CalibrationViewModel.BandRadiusPixels):
                Dispatcher.Invoke(() => PositionTarget(_viewModel!.CurrentTargetPosition, animate: false));
                break;
            case nameof(CalibrationViewModel.TargetProgress):
            case nameof(CalibrationViewModel.IsOnTarget):
                Dispatcher.Invoke(UpdateOnTargetFeedback);
                break;
            case nameof(CalibrationViewModel.State):
                Dispatcher.Invoke(() =>
                {
                    UpdateVisualState(_viewModel!.State, _viewModel.ProgressPercent);
                    // StatusOf is state-dependent — the set is published while the session is still
                    // Idle, so without this every marker would stay in its "upcoming" styling and the
                    // first target would be drawn twice.
                    UpdateTargetSetStates();
                });
                break;
            case nameof(CalibrationViewModel.TargetColor):
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var brush = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString(_viewModel!.TargetColor));
                        TargetDot.Fill = brush;
                    }
                    catch { }
                });
                break;
        }
    }

    private void PositionTarget(PhysicalPoint physicalPoint, bool animate)
    {
        var logical = physicalPoint.ToLogical();

        // The dot: the thing to aim at, from the setting that until now was read by nothing.
        var dotDiameterLogical = DiameterToLogical(_viewModel?.TargetRadiusPixels ?? 20);
        TargetDot.Width = dotDiameterLogical;
        TargetDot.Height = dotDiameterLogical;
        if (TargetDot.RenderTransform is ScaleTransform dotScale)
        {
            dotScale.CenterX = dotDiameterLogical / 2;
            dotScale.CenterY = dotDiameterLogical / 2;
        }
        CentreOnLayerOrigin(TargetDot, dotDiameterLogical);

        // The rings: the tolerance actually enforced. Converted from physical px through the same
        // DPI helper the dot uses, because the collector's radius is in physical pixels and this
        // canvas is in DIPs — drawing the raw number would be right only at 100 % scaling.
        var bandDiameterLogical = DiameterToLogical(_viewModel?.BandRadiusPixels ?? 0);
        foreach (var ring in new[] { ToleranceRing, ProgressRing })
        {
            ring.Width = bandDiameterLogical;
            ring.Height = bandDiameterLogical;
            CentreOnLayerOrigin(ring, bandDiameterLogical);
        }

        MoveActiveLayerTo(logical, animate);
        UpdateOnTargetFeedback();
    }

    /// <summary>
    /// Places a visual so its centre sits on <see cref="ActiveTargetLayer"/>'s origin. The layer then
    /// carries the screen position for all three at once — which is what lets an advance animate one
    /// pair of coordinates instead of three that could drift apart mid-slide.
    /// </summary>
    private static void CentreOnLayerOrigin(FrameworkElement element, double diameter)
    {
        Canvas.SetLeft(element, -(diameter / 2));
        Canvas.SetTop(element, -(diameter / 2));
    }

    /// <summary>
    /// Moves the active target's visuals to <paramref name="logicalCentre"/>, sliding when this is an
    /// advance and snapping otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The motion IS the instruction.</b> The previous overlay drew one dot and teleported it, so
    /// after every advance the user had to find it again. A short slide points at the new location
    /// using movement, which peripheral vision detects without being looked at directly — the one
    /// channel available to someone who cannot read a cue and hold a fixation at the same time.
    /// </para>
    /// <para>
    /// <b>Presentation only.</b> The view model has already moved to the new target and the collector is
    /// already gating on it before this runs; nothing here feeds back into either. The cost is that for
    /// the length of the slide the drawn ring trails the enforced band, so it is kept short — and the
    /// destination is not a mystery in the meantime, because every target is on screen already.
    /// </para>
    /// <para>
    /// <b>The animation is released on completion, deliberately.</b> A held WPF animation outranks
    /// later direct writes to the same property: leaving it in place would silently swallow the next
    /// <c>Canvas.SetLeft</c> — including the one that repositions after a band-radius change — and the
    /// symptom would be a target stuck at the previous location with no error anywhere.
    /// </para>
    /// </remarks>
    private void MoveActiveLayerTo(LogicalPoint logicalCentre, bool animate)
    {
        var currentLeft = Canvas.GetLeft(ActiveTargetLayer);
        var currentTop = Canvas.GetTop(ActiveTargetLayer);
        bool hasPrevious = !double.IsNaN(currentLeft) && !double.IsNaN(currentTop);

        if (!animate || !hasPrevious || !IsVisible)
        {
            SnapActiveLayerTo(logicalCentre);
            return;
        }

        // Each slide claims a generation. A handler whose generation has been superseded does nothing:
        // without that, a fast advance (or a band-radius change landing mid-slide) lets the OLD
        // animation's Completed fire after the new one started and write its captured destination,
        // parking the target at the previous position. This repo has been bitten by held animations
        // outranking direct writes before; the same class of bug, one step removed.
        var generation = ++_slideGeneration;
        Slide(Canvas.LeftProperty, currentLeft, logicalCentre.X);
        Slide(Canvas.TopProperty, currentTop, logicalCentre.Y);

        void Slide(DependencyProperty property, double from, double to)
        {
            var animation = new DoubleAnimation(from, to, new Duration(TargetSlideDuration))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd,
            };
            animation.Completed += (_, _) =>
            {
                if (generation != _slideGeneration) return;
                ActiveTargetLayer.BeginAnimation(property, null);
                ActiveTargetLayer.SetValue(property, to);
            };
            ActiveTargetLayer.BeginAnimation(property, animation);
        }
    }

    private void SnapActiveLayerTo(LogicalPoint logicalCentre)
    {
        // Also claims a generation: a snap must invalidate any slide still in flight, or that slide's
        // completion would drag the layer back to where it was heading.
        _slideGeneration++;
        ActiveTargetLayer.BeginAnimation(Canvas.LeftProperty, null);
        ActiveTargetLayer.BeginAnimation(Canvas.TopProperty, null);
        Canvas.SetLeft(ActiveTargetLayer, logicalCentre.X);
        Canvas.SetTop(ActiveTargetLayer, logicalCentre.Y);
    }

    /// <summary>
    /// Draws one marker for every target in the session, so the next location is on screen before the
    /// current one finishes.
    /// </summary>
    /// <remarks>
    /// This is the other half of the fix, and the more important half: the slide shows where the target
    /// went, but only because the destination was already visible does the user stop having to search.
    /// Markers are deliberately small and quiet — they are a map, not competition for the active
    /// target, which keeps the full-size band ring and the pulse.
    /// </remarks>
    private void BuildTargetSet()
    {
        TargetSetCanvas.Children.Clear();
        _targetSetMarkers.Clear();

        var positions = _viewModel?.TargetPositions ?? Array.Empty<PhysicalPoint>();
        var diameter = Math.Max(6, DiameterToLogical(_viewModel?.TargetRadiusPixels ?? 20) * 0.5);

        foreach (var position in positions)
        {
            var logical = position.ToLogical();
            var marker = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(marker, logical.X - (diameter / 2));
            Canvas.SetTop(marker, logical.Y - (diameter / 2));
            TargetSetCanvas.Children.Add(marker);
            _targetSetMarkers.Add(marker);
        }

        UpdateTargetSetStates();
    }

    /// <summary>
    /// Restyles each marker for what its target is doing now. Three states, distinguished by shape and
    /// weight rather than by colour alone, and none of them carrying text.
    /// </summary>
    /// <remarks>
    /// The CURRENT target's marker is hidden outright: the active layer already draws that position at
    /// full size with the band ring, and two marks on one spot would read as two targets. That is also
    /// what keeps "only the current target accepts gaze" visually unambiguous — exactly one place on
    /// screen ever has a ring around it.
    /// </remarks>
    private void UpdateTargetSetStates()
    {
        if (_viewModel is null) return;

        for (int i = 0; i < _targetSetMarkers.Count; i++)
        {
            var marker = _targetSetMarkers[i];
            switch (_viewModel.StatusOf(i))
            {
                case CalibrationTargetStatus.Current:
                    marker.Visibility = Visibility.Hidden;
                    break;

                case CalibrationTargetStatus.Done:
                    // Filled and settled: progress through the set, readable at a glance.
                    marker.Visibility = Visibility.Visible;
                    marker.Fill = Tokens.BrushAccentSuccess;
                    marker.Stroke = Tokens.BrushAccentSuccess;
                    marker.Opacity = 0.55;
                    break;

                default:
                    // Hollow and faint: present, clearly not the one being asked for.
                    marker.Visibility = Visibility.Visible;
                    marker.Fill = null;
                    marker.Stroke = Tokens.BrushTextDim;
                    marker.Opacity = 0.45;
                    break;
            }
        }
    }

    /// <summary>
    /// Physical-pixel RADIUS to logical DIAMETER, via the app's DPI scale.
    /// <para>
    /// Takes the X scale only, so a display whose horizontal and vertical DPI scaling differ would get
    /// a circle sized by its width. That is deliberate rather than overlooked: these are CIRCLES, and
    /// the band they represent is a radial distance — one number, equal on both axes by definition
    /// (see <c>CalibrationGeometry.BandRadiusPx</c>, which scales by the shorter side). Honouring two
    /// scales would mean drawing an ellipse for a tolerance that is not elliptical, which is a worse
    /// lie than a slightly wrong size. Windows does not expose independent per-axis scaling in any
    /// case, and the rest of this file's geometry (<c>ToLogical</c> on the target position) makes the
    /// same assumption.
    /// </para>
    /// </summary>
    private static double DiameterToLogical(double radiusPhysical)
    {
        var edge = new PhysicalPoint(radiusPhysical * 2, radiusPhysical * 2).ToLogical();
        return Math.Max(2, edge.X);
    }

    /// <summary>
    /// Makes "inside the ring" and "about to leave" legible without text. The progress ring brightens
    /// and thickens as the view model's <c>TargetProgress</c> rises — in-band samples accumulating in a
    /// normal calibration, the elapsed share of the fixed window in the measure-only probe — and the
    /// whole ring dims the moment gaze leaves the band. So the user learns what advances the target by
    /// watching it, which is the only channel available to someone who cannot read a number and hold a
    /// fixation at the same time.
    /// </summary>
    private void UpdateOnTargetFeedback()
    {
        if (_viewModel is null) return;

        var progress = Math.Clamp(_viewModel.TargetProgress, 0, 1);
        ProgressRing.Opacity = progress <= 0 ? 0 : 0.35 + (0.65 * progress);
        ProgressRing.StrokeThickness = 4 + (8 * progress);

        // The tolerance ring itself is the on/off signal: solid while gaze is inside it, faint while
        // it is not. Deliberately a property of the RING rather than of the dot — the ring is the
        // thing whose meaning is being taught.
        ToleranceRing.Opacity = _viewModel.IsOnTarget ? 0.9 : 0.3;
        ToleranceRing.StrokeThickness = _viewModel.IsOnTarget ? 4 : 2;
    }

    public void UpdateVisualState(CalibrationState state, double progress)
    {
        StopAutoDismiss();

        switch (state)
        {
            case CalibrationState.Collecting:
                Backdrop.Visibility = Visibility.Visible;
                TargetCanvas.Visibility = Visibility.Visible;
                CompletionPanel.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Visible;
                CancelHint.Visibility = Visibility.Visible;
                Show();
                // Mouse passes through (never blocks the screen), but keep keyboard focus so Esc
                // cancels. MUST come after Show(): SetOverlayInteraction early-returns while the
                // HWND is IntPtr.Zero, and the window is only ever constructed — never shown — at
                // startup, so on the FIRST calibration the HWND is created by this very Show().
                // Called before it, this no-opped and OverlayWindow.OnSourceInitialized then
                // applied focusable:false, leaving the first run unfocusable: Activate() below was
                // blocked by WS_EX_NOACTIVATE and Esc did nothing, despite the visible hint.
                SetOverlayInteraction(clickThrough: true, focusable: true);
                Activate();
                var idx = _viewModel?.CurrentTargetIndex ?? 0;
                var total = _viewModel?.TotalTargets ?? 0;
                ProgressText.Text = StringTable.Current.T("calibration.progress.lookAtDot", idx + 1, total);
                StartPulse();
                break;

            case CalibrationState.Training:
                TargetCanvas.Visibility = Visibility.Collapsed;
                CancelHint.Visibility = Visibility.Collapsed;
                StopPulse();
                ProgressText.Text = _viewModel?.IsMeasureOnly == true
                    ? StringTable.Current.T("calibration.progress.recording")
                    : StringTable.Current.T("calibration.progress.computing");
                break;

            case CalibrationState.Active:
                ShowCompletion(goodResult: true);
                break;

            case CalibrationState.Failed:
                ShowCompletion(goodResult: false);
                break;

            default:
                TargetCanvas.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
                CancelHint.Visibility = Visibility.Collapsed;
                Backdrop.Visibility = Visibility.Collapsed;
                CompletionPanel.Visibility = Visibility.Collapsed;
                StopPulse();
                StopAutoDismiss();
                break;
        }
    }

    private void ShowCompletion(bool goodResult)
    {
        TargetCanvas.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
        Backdrop.Visibility = Visibility.Visible;
        CompletionPanel.Visibility = Visibility.Visible;
        StopPulse();

        // Make the window opaque + focusable + hit-testable so Retry/Accept and Esc work.
        // After Show() for the same reason as the Collecting case above — Failed can be reached
        // without a preceding Collecting (a session that never starts), so the HWND is not
        // guaranteed to exist by the time we get here.
        Show();
        SetOverlayInteraction(clickThrough: false, focusable: true);
        Activate();
        Focus();

        // The copy and the visibility rules are a pure function (CalibrationCompletion), so the
        // measure-only branch — no "failed", no Retry — has a headless test without this window's
        // Show()/Activate()/Focus().
        var presentation = CalibrationCompletion.Describe(
            _viewModel?.IsMeasureOnly == true,
            goodResult,
            _viewModel?.MeanErrorPixels ?? 0,
            // Quality % is computed in the VM against the physical-pixel screen diagonal
            // (CalibrationData.ComputeQualityPercent) — DPI-correct, single source of truth.
            _viewModel?.QualityPercent ?? 0,
            _viewModel?.ErrorText);

        CompletionSymbol.Text = presentation.Symbol;
        CompletionSymbol.Foreground = presentation.Tone == CompletionTone.Success
            ? Tokens.BrushAccentSuccess
            : Tokens.BrushAccentWarning;
        CompletionTitle.Text = presentation.Title;
        CompletionError.Text = presentation.Detail;
        CompletionHint.Visibility = presentation.HintVisible ? Visibility.Visible : Visibility.Collapsed;
        RetryPanel.Visibility = presentation.RetryVisible ? Visibility.Visible : Visibility.Collapsed;
        CancelHint.Visibility = presentation.CancelHintVisible ? Visibility.Visible : Visibility.Collapsed;
        StartAutoDismiss(presentation.AutoDismissMs, DismissAndReset);
    }

    /// <summary>Dismisses the overlay and returns the VM to Idle so re-calibration is possible.</summary>
    private void DismissAndReset()
    {
        _viewModel?.Acknowledge();
        DismissCalibration();
    }

    public void DismissCalibration()
    {
        Backdrop.Visibility = Visibility.Collapsed;
        TargetCanvas.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
        CompletionPanel.Visibility = Visibility.Collapsed;
        CancelHint.Visibility = Visibility.Collapsed;
        RetryPanel.Visibility = Visibility.Collapsed;
        StopPulse();
        StopAutoDismiss();
        // Restore the default non-activating overlay style.
        SetOverlayInteraction(clickThrough: IsClickThrough, focusable: false);
        Hide();
    }

    private void StartPulse()
    {
        StopPulse();
        _pulseAnimation = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var duration = new Duration(TimeSpan.FromMilliseconds(600));

        var scaleX = new DoubleAnimation(0.8, 1.2, duration) { AutoReverse = true };
        Storyboard.SetTarget(scaleX, TargetDot);
        Storyboard.SetTargetProperty(scaleX,
            new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        _pulseAnimation.Children.Add(scaleX);

        // Animate Y too so the dot pulses uniformly instead of stretching horizontally.
        var scaleY = new DoubleAnimation(0.8, 1.2, duration) { AutoReverse = true };
        Storyboard.SetTarget(scaleY, TargetDot);
        Storyboard.SetTargetProperty(scaleY,
            new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        _pulseAnimation.Children.Add(scaleY);

        _pulseAnimation.Begin();
    }

    private void StopPulse()
    {
        _pulseAnimation?.Stop();
        _pulseAnimation = null;
    }

    private void StartAutoDismiss(int delayMs, Action onDismiss)
    {
        StopAutoDismiss();
        _autoDismissTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(delayMs), DispatcherPriority.Normal,
            (_, _) => { StopAutoDismiss(); onDismiss(); }, Dispatcher);
        _autoDismissTimer.Start();
    }

    private void StopAutoDismiss()
    {
        _autoDismissTimer?.Stop();
        _autoDismissTimer = null;
    }
}
