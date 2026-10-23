namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

public partial class CalibrationWindow : OverlayWindow, ICalibrationOverlay
{
    /// <summary>
    /// The dwell id of the gaze Cancel target. <c>nameof</c> rather than a quoted id: this window's
    /// target id is not UI text, and the literal ratchet rightly treats a string in
    /// <c>new DwellTarget(...)</c> as a text sink.
    /// </summary>
    internal const string CancelTargetId = nameof(CancelDwellButton);

    /// <summary>
    /// Floor for the Cancel target's dwell. Longer than every standard surface on purpose: a glance
    /// between two calibration targets passes through this part of the screen, and that must never
    /// end the session.
    /// </summary>
    internal const int MinimumCancelDwellMs = 1000;

    /// <summary>
    /// Clearance in DIP between the Cancel disc and the progress card's measured rectangle. The card
    /// is window chrome the disc must not cover; without this, at 150 % on a 1080p display with three
    /// points the top-edge placement and the top-centre card overlap (review 01, LOW 2).
    /// </summary>
    private const double ProgressCardClearanceDip = 12;

    private readonly DwellDetector _cancelDwell;
    private readonly DwellOutlineOverlay _cancelOutline;
    private readonly DwellSettings _dwellSettings;
    private readonly IMessenger _messenger;
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

    /// <summary>Creates the window. It is constructed at startup but only shown when a session
    /// starts, so nothing here puts anything on screen.</summary>
    /// <param name="dwellSettings">The live dwell settings, read per session for the Cancel
    /// target's <c>max(1000 ms, toolbar dwell)</c>. Null uses the defaults (toolbar 700 ms, so the
    /// 1000 ms floor).</param>
    /// <param name="messenger">The bus the dwell-completion cue is published on; null is the app
    /// messenger.</param>
    public CalibrationWindow(DwellSettings? dwellSettings = null, IMessenger? messenger = null)
    {
        _dwellSettings = dwellSettings ?? new DwellSettings();
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        InitializeComponent();

        // RestartNoticeWindow's pattern: this window has its own detector and no shared registrar.
        var cancelLabel = CancelDwellLabel;
        CancelDwellButton.Child = null;
        _cancelOutline = DwellOutlineOverlay.Attach(CancelDwellButton, cancelLabel);
        _cancelDwell = new DwellDetector(cooldownMs: 0);
        _cancelDwell.TargetActivated += OnCancelDwellActivated;
        _cancelDwell.TargetProgress += (_, progress) => _cancelOutline.SetProgress(progress);

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
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.GazeSampleReceived -= OnCalibrationGaze;
        }

        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        // The Cancel dwell is fed from the session's OWN gaze path (CalibrationViewModel.OnRawGaze),
        // not from a second provider subscription: one gaze stream per session, so the sample the
        // collector sees and the sample the dwell sees can never disagree.
        viewModel.GazeSampleReceived += OnCalibrationGaze;
        // Round 3, R1: TargetColor is set ONCE in the view model's constructor, without
        // a change notification — so the handler below never fired for the persisted
        // value and the dot kept the XAML's theme accent forever (on main, the saved
        // colour has never been shown). Apply it at attach; later changes still ride
        // the handler.
        ApplyTargetDotColor();
    }

    /// <summary>The Cancel target's dwell time for the current settings: <c>max(1000 ms, toolbar dwell)</c>.</summary>
    internal int CancelDwellMs => Math.Max(MinimumCancelDwellMs, _dwellSettings.ToolbarButtonDwellMs);

    /// <summary>The Cancel target's detector — the test seam for the registered target and its geometry.</summary>
    internal DwellDetector CancelDwellDetector => _cancelDwell;

    /// <summary>The Cancel target's dwell outline — the test seam for visible progress.</summary>
    internal DwellOutlineOverlay CancelDwellOutline => _cancelOutline;

    /// <summary>
    /// Feeds one raw sample to the Cancel detector. Public-to-the-assembly so a headless test can
    /// time a dwell without a provider, exactly as <c>RestartNoticeWindow.OnGaze</c> does.
    /// </summary>
    /// <param name="gaze">Gaze position in physical screen pixels.</param>
    /// <param name="timestampMicroseconds">Sample timestamp in microseconds.</param>
    internal void OnGaze(PhysicalPoint gaze, long timestampMicroseconds)
    {
        if (_viewModel?.State != CalibrationState.Collecting) return;
        _cancelDwell.Update(gaze, timestampMicroseconds);
    }

    /// <summary>
    /// Puts the Cancel target where this state needs it, or takes it away. Its geometry comes from
    /// the view model — the same values the collection gate excludes — and the drawn disc is
    /// centred on the registered hit circle, so the target the user sees is the target gaze is
    /// evaluated against.
    /// </summary>
    /// <param name="state">The calibration state being drawn. The target exists only in
    /// <see cref="CalibrationState.Collecting"/>: Training hides the target canvas (the fit is
    /// running) and every other state has no session to cancel.</param>
    internal void SyncCancelTarget(CalibrationState state)
    {
        if (state != CalibrationState.Collecting || _viewModel is null)
        {
            ClearCancelTarget();
            return;
        }

        UpdateCancelTarget();
    }

    /// <summary>The progress-card keep-out last used, in physical pixels — the LOW-2 test seam.</summary>
    internal PhysicalRect? LastCancelKeepOut { get; private set; }

    /// <summary>
    /// Computes the chrome-aware placement, publishes it to the view model (the collection gate
    /// excludes the SAME circle), and registers and draws the target.
    /// </summary>
    private void UpdateCancelTarget()
    {
        if (_viewModel is null || _viewModel.CancelTargetRadiusPixels <= 0)
        {
            ClearCancelTarget();
            return;
        }

        var radiusPx = _viewModel.CancelTargetRadiusPixels;
        var keepOut = ProgressCardKeepOut();
        LastCancelKeepOut = keepOut;

        var centre = CalibrationCancelPlacement.GetPosition(
            _viewModel.TargetPositions,
            _viewModel.ScreenSize,
            radiusPx,
            keepOut is { } rectangle ? [rectangle] : null);
        _viewModel.SetCancelTarget(centre, radiusPx);

        var dwellMs = CancelDwellMs;
        var registered = FindCancelTarget();
        if (registered is null
            || registered.Center != centre
            || Math.Abs(registered.Radius - radiusPx) > 1e-6
            || registered.DwellTimeMicroseconds != dwellMs * MathConstants.MicrosecondsPerMillisecond)
        {
            _cancelDwell.ClearTargets();
            _cancelDwell.AddTarget(new DwellTarget(CancelTargetId, centre, radiusPx, dwellMs));
            _cancelOutline.SetProgress(0);
        }

        var logical = centre.ToLogical();
        Canvas.SetLeft(CancelDwellButton, logical.X - CalibrationCancelPlacement.HitRadiusDip);
        Canvas.SetTop(CancelDwellButton, logical.Y - CalibrationCancelPlacement.HitRadiusDip);
        CancelDwellButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The progress card's own rectangle, in physical pixels, inflated by its clearance; null when
    /// it has no measurable size. Measured from the WIDEST line the session can show, so advancing
    /// the target (which changes the index and therefore the card's width) cannot move the disc and
    /// restart its dwell mid-session.
    /// </summary>
    private PhysicalRect? ProgressCardKeepOut()
    {
        if (_viewModel is null) return null;

        ProgressCard.Visibility = Visibility.Visible;
        var previous = ProgressText.Text;
        try
        {
            var total = Math.Max(1, _viewModel.TotalTargets);
            var widest = StringTable.Current.T("calibration.progress.lookAtDot", total, total);
            var recording = StringTable.Current.T("calibration.progress.recording");
            var width = 0.0;
            var height = 0.0;
            foreach (var text in new[] { widest, recording })
            {
                ProgressText.Text = text;
                // A changed TextBlock dirties only itself; the Border's constraint is unchanged from
                // the previous Measure(∞), so WPF would return its PREVIOUS DesiredSize (the
                // hidden-window measure trap, review 02 LOW A). Invalidating both forces a fresh
                // pass on a window reused across sessions.
                ProgressText.InvalidateMeasure();
                ProgressCard.InvalidateMeasure();
                ProgressCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width = Math.Max(width, ProgressCard.DesiredSize.Width);
                height = Math.Max(height, ProgressCard.DesiredSize.Height);
            }

            if (width <= 0 || height <= 0) return null;

            // DesiredSize already includes the card's Margin (WPF adds it to the margin box), so the
            // box is centred at y = 0 and its top edge must NOT add Margin.Top a second time.
            var leftDip = (Width - width) / 2;
            const double topDip = 0;
            const double padding = ProgressCardClearanceDip;
            return new PhysicalRect(
                DpiHelper.LogicalToPhysicalX(leftDip - padding),
                DpiHelper.LogicalToPhysicalY(topDip - padding),
                DpiHelper.LogicalToPhysicalX(width + (2 * padding)),
                DpiHelper.LogicalToPhysicalY(height + (2 * padding)));
        }
        finally
        {
            ProgressText.Text = previous;
        }
    }

    private DwellTarget? FindCancelTarget()
    {
        foreach (var target in _cancelDwell.Targets)
        {
            if (target.Id == CancelTargetId) return target;
        }

        return null;
    }

    private void ClearCancelTarget()
    {
        _cancelDwell.ClearTargets();
        _cancelOutline.SetProgress(0);
        CancelDwellButton.Visibility = Visibility.Collapsed;
        LastCancelKeepOut = null;
    }

    // Post-effect publish, like every dwell surface: the session ends first, then the cue confirms.
    private void OnCancelDwellActivated(string id)
    {
        if (id != CancelTargetId) return;
        ClearCancelTarget();
        _viewModel?.Cancel();
        _messenger.Send(new DwellActivationMessage());
    }

    /// <summary>
    /// The view model's per-sample gaze event, which arrives on the tracker thread. The detector is
    /// single-threaded by contract, so the sample is marshalled to this window's dispatcher; a test
    /// (or a same-thread provider) feeds it straight through.
    /// </summary>
    private void OnCalibrationGaze(GazePoint gaze)
    {
        var point = new PhysicalPoint(gaze.X, gaze.Y);
        if (Dispatcher.CheckAccess())
        {
            OnGaze(point, gaze.TimestampMicroseconds);
            return;
        }

        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => OnGaze(point, gaze.TimestampMicroseconds)));
    }

    /// <summary>
    /// Parses and applies the user-chosen target colour. Shared by the attach path and the
    /// <see cref="CalibrationViewModel.TargetColor"/> change handler so both produce the
    /// same brush. Unparseable values are swallowed exactly as before: the dot keeps its
    /// last good colour rather than blanking mid-calibration.
    /// </summary>
    private void ApplyTargetDotColor()
    {
        try
        {
            var brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_viewModel!.TargetColor));
            TargetDot.Fill = brush;
        }
        catch { }
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
                Dispatcher.Invoke(ApplyTargetDotColor);
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
        // Halos share the ring geometry; their thicker stroke is what shows the
        // accent/halo edge (§4.1.3). Sized here with the rings they back. NOTE: the
        // house convention is "halo stays at Opacity=1 so the edge holds" (Scroll-
        // OverlayWindow, ManualSelection, DragTrail); these halos deliberately dim
        // WITH their rings instead, because opacity IS the on/off-target signal here —
        // an opaque white halo would read "solid" exactly when the user is off target.
        foreach (var ring in new[] { ToleranceRing, ProgressRing, ToleranceHalo, ProgressHalo })
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
                    // Stroke is the HALO, not a second success band (round 3, m1): these
                    // markers sit straight on the black scrim, where Daylight's dark
                    // success fill measured ~1.5:1. The rim makes the fill read on every
                    // theme's scrim, same idiom as the dot and the rings. NOTE (round 4,
                    // m1): on Daylight the rim dominates and this state reads as a white
                    // RING while the upcoming state below reads as a filled light DISC —
                    // the dark themes' filled/hollow grammar inverts. Recorded in §10.8;
                    // no single-ellipse fix exists (every Daylight token but the halo is
                    // dark on the black scrim).
                    marker.Visibility = Visibility.Visible;
                    marker.Fill = Tokens.BrushAccentSuccess;
                    marker.Stroke = Tokens.BrushStrokeHalo;
                    marker.StrokeThickness = 1.5;
                    marker.Opacity = 0.55;
                    break;

                default:
                    // Hollow and faint: present, clearly not the one being asked for.
                    // Interior carries the halo colour for the same scrim reason — the
                    // dim rim keeps the deliberate quietness, the interior guarantees an
                    // edge against the backdrop on both polarities.
                    marker.Visibility = Visibility.Visible;
                    marker.Fill = Tokens.BrushStrokeHalo;
                    marker.Stroke = Tokens.BrushTextDim;
                    marker.StrokeThickness = 1.5;
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
        ProgressHalo.Opacity = ProgressRing.Opacity;
        ProgressHalo.StrokeThickness = ProgressRing.StrokeThickness + 2;

        // The tolerance ring itself is the on/off signal: solid while gaze is inside it, faint while
        // it is not. Deliberately a property of the RING rather than of the dot — the ring is the
        // thing whose meaning is being taught.
        ToleranceRing.Opacity = _viewModel.IsOnTarget ? 0.9 : 0.3;
        ToleranceRing.StrokeThickness = _viewModel.IsOnTarget ? 4 : 2;
        ToleranceHalo.Opacity = ToleranceRing.Opacity;
        ToleranceHalo.StrokeThickness = ToleranceRing.StrokeThickness + 2;
    }

    public void UpdateVisualState(CalibrationState state, double progress)
    {
        StopAutoDismiss();

        // Hoisted here so both progress-bearing states read the same values through
        // ProgressTextFor; the states that draw no progress line ignore them. These are the view
        // model's raw reads — the 0-based index included — so the display arithmetic stays in the
        // helper.
        var idx = _viewModel?.CurrentTargetIndex ?? 0;
        var total = _viewModel?.TotalTargets ?? 0;
        var measureOnly = _viewModel?.IsMeasureOnly == true;

        switch (state)
        {
            case CalibrationState.Collecting:
                Backdrop.Visibility = Visibility.Visible;
                TargetCanvas.Visibility = Visibility.Visible;
                CompletionCard.Visibility = Visibility.Collapsed;
                ProgressCard.Visibility = Visibility.Visible;
                CancelCard.Visibility = Visibility.Visible;
                ProgressText.Text = ProgressTextFor(
                    state, index: idx, total: total, measureOnly: measureOnly);
                // AFTER the progress text: the placement must avoid the card it measures. This call
                // is the production registration path — the only one that puts the target on
                // screen — and the state route (the view model's State property and the
                // coordinator's message) both end here.
                SyncCancelTarget(state);
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
                StartPulse();
                break;

            case CalibrationState.Training:
                ClearCancelTarget();
                TargetCanvas.Visibility = Visibility.Collapsed;
                CancelCard.Visibility = Visibility.Collapsed;
                StopPulse();
                ProgressText.Text = ProgressTextFor(
                    state, index: idx, total: total, measureOnly: measureOnly);
                break;

            case CalibrationState.Active:
                ClearCancelTarget();
                ShowCompletion(goodResult: true);
                break;

            case CalibrationState.Failed:
                ClearCancelTarget();
                ShowCompletion(goodResult: false);
                break;

            default:
                ClearCancelTarget();
                TargetCanvas.Visibility = Visibility.Collapsed;
                ProgressCard.Visibility = Visibility.Collapsed;
                CancelCard.Visibility = Visibility.Collapsed;
                Backdrop.Visibility = Visibility.Collapsed;
                CompletionCard.Visibility = Visibility.Collapsed;
                StopPulse();
                StopAutoDismiss();
                break;
        }
    }

    /// <summary>
    /// The progress line for one state, as pure copy — the window draws it, but the text choice
    /// needs no HWND. <paramref name="index"/> is the VIEW MODEL's 0-based target index and the
    /// helper adds the 1 itself, so the display arithmetic lives inside this pinned seam and the
    /// call site passes values through (with named arguments, so a swap is visible in review). A
    /// state that shows no progress line returns empty.
    /// </summary>
    internal static string ProgressTextFor(
        CalibrationState state, int index, int total, bool measureOnly) => state switch
    {
        CalibrationState.Collecting =>
            StringTable.Current.T("calibration.progress.lookAtDot", index + 1, total),
        CalibrationState.Training when measureOnly => StringTable.Current.T("calibration.progress.recording"),
        CalibrationState.Training => StringTable.Current.T("calibration.progress.computing"),
        _ => string.Empty,
    };

    private void ShowCompletion(bool goodResult)
    {
        TargetCanvas.Visibility = Visibility.Collapsed;
        ProgressCard.Visibility = Visibility.Collapsed;
        Backdrop.Visibility = Visibility.Visible;
        CompletionCard.Visibility = Visibility.Visible;
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
        CancelCard.Visibility = presentation.CancelHintVisible ? Visibility.Visible : Visibility.Collapsed;
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
        ClearCancelTarget();
        Backdrop.Visibility = Visibility.Collapsed;
        TargetCanvas.Visibility = Visibility.Collapsed;
        ProgressCard.Visibility = Visibility.Collapsed;
        CompletionCard.Visibility = Visibility.Collapsed;
        CancelCard.Visibility = Visibility.Collapsed;
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
