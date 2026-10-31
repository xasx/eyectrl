namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public enum DragSubState
{
    FreeDrag,
    Settling,
    ManualSelectionActive
}

/// <summary>
/// One exit hit circle during a drag, in physical px: the edge bar's Cancel and Back buttons (the
/// static zones <c>DragCoordinator</c> supplies from the same anchor the bar draws at) and the
/// toolbar Pause button (supplied by the LIVE provider, see
/// <see cref="DragTrailViewModel.SetPauseExitZoneProvider"/>, because that button can move, appear
/// or stop being dwellable mid-drag). While the gaze is inside one, the drag must neither settle
/// nor drop: that rest belongs to the button's own dwell (Cancel, Back or Pause).
/// </summary>
public readonly record struct DragExitZone(PhysicalPoint Center, double RadiusPhysical);

/// <summary>
/// What the overlay draws for the drag's gaze-following drop target (C2): the hit circle itself
/// (centre and the detector's LIVE radius, in physical px, so drawn = hit), the release dwell's
/// progress, and whether that progress is held by the pause grace (drawn dimmed).
/// </summary>
public readonly record struct DragReleaseDisc(
    PhysicalPoint Center, double RadiusPhysical, double Progress, bool Paused);

/// <summary>
/// C6: how one gaze sample sits relative to the screen's off-screen bands (the same two bands C4
/// applies to the repeat dwell). <see cref="OnScreen"/> and <see cref="Withheld"/> both keep the
/// current rest alive; only <see cref="Exit"/> is the A5 H1 clean break (reset plus restart spot).
/// </summary>
internal enum ScreenSampleBand
{
    /// <summary>Inside the published bounds plus the tolerance: an ordinary sample.</summary>
    OnScreen,

    /// <summary>
    /// Past the tolerance but inside the disarm margin: the summon gesture has not started, so the
    /// sample neither earns credit nor breaks the rest — it is withheld (C6).
    /// </summary>
    Withheld,

    /// <summary>Past the disarm margin, or inside a Cancel/Back/Pause exit: a clean break.</summary>
    Exit,
}

public partial class DragTrailViewModel : DwellOverlayViewModelBase,
    IRecipient<StartDragTrailMessage>,
    IRecipient<DragCancelMessage>,
    IRecipient<DragDismissManualSelectionMessage>
{
    private const string ReleaseTargetId = "Drag_release";

    /// <summary>
    /// C2: how far, in hit radii, the gaze may wander from the spot where the current release dwell
    /// started and still be the SAME rest. Inside it a gaze that crosses the target's edge re-centres
    /// the target and keeps the dwell (drift and the owner's random-walk wander); beyond it the gaze
    /// has moved to a new spot and the dwell restarts there (PX2/R4). A saccade is caught sooner,
    /// by the step rule: a single-sample step of more than one radius is a new spot whatever the
    /// distance from the spot (fixup-01 R2). The same distance arms the drop after the drag starts
    /// (the leave-before-arm latch, fixup-01 R1).
    /// </summary>
    internal const double SameSpotRadii = 1.5;

    /// <summary>
    /// C2 fixup-01 R2: a move of more than one radius from ANY of the previous this-many
    /// dwell-eligible samples is a saccade to a new spot, and the dwell restarts there. Not a
    /// single-sample rule: the cursor filter spreads a re-aim over about three samples, and on the
    /// owner's recording no re-aim inside a drop's final 600 ms was a one-sample step (82 were
    /// 3-sample moves). Drift never trips it: a radius in 100 ms is at least 1000 px/s at 200 %.
    /// </summary>
    internal const int SaccadeWindowSamples = 3;

    /// <summary>
    /// A silence at least this long between two dwell-eligible samples clears the saccade history
    /// (R2): the gaze was not reported, so the samples before it say nothing about a step. The
    /// detector's own continuity bound (<c>DwellDetector</c>'s 150 ms credited-gap clamp).
    /// </summary>
    private const long HistorySilenceUs = 150_000;

    private readonly EyeCtrlSettings _settings;
    private readonly FixationDetector _settleDetector;
    private IReadOnlyList<DragExitZone> _exitZones = Array.Empty<DragExitZone>();
    private Func<DragExitZone?>? _pauseExitZone;
    private PhysicalRect? _screenBounds;
    private double _screenTolerancePhysical;
    private bool _inExitZone;

    // C2: the spot the current release dwell belongs to — where the target was placed when the
    // dwell last restarted. Re-centres within SameSpotRadii of it keep the dwell.
    private PhysicalPoint _releaseSpot;

    // C2 fixup-01 R3: the running sum of the credited (inside) samples since _releaseSpot was set.
    // The drop lands at their centroid — the owner's hold is a random walk, so its centre is the
    // spot they mean, not whichever sample happened to complete the dwell.
    private double _restSumX;
    private double _restSumY;
    private int _restCount;

    // C2 fixup-01 R1: the leave-before-arm latch. The drag starts while the gaze still rests on the
    // radial menu's Drag item, and the forgiving target would drop the item THERE. The release
    // dwell stays disarmed until the gaze has left SameSpotRadii radii from the FIRST sample after
    // the start — not from the grab point, which is a menu-ring away from that gaze — and then stays
    // armed for the rest of the drag.
    private PhysicalPoint? _armAnchor;
    private bool _releaseArmed;

    // C2 fixup-01 R2: the last SaccadeWindowSamples dwell-eligible samples of the current rest, for
    // the saccade rule. Cleared wherever the spot is reset and across a gap or a silence, so a
    // resume after a look-away is never compared against samples from before it.
    private readonly Queue<PhysicalPoint> _recentGaze = new(SaccadeWindowSamples + 1);
    private long? _lastEligibleTimestampUs;

    [ObservableProperty] private bool _isDragActive;
    [ObservableProperty] private PhysicalPoint _grabOrigin;
    [ObservableProperty] private PhysicalPoint _cursorPosition;
    [ObservableProperty] private DragSubState _dragState = DragSubState.FreeDrag;
    [ObservableProperty] private double _settleProgress;
    [ObservableProperty] private bool _showBack;
    [ObservableProperty] private PhysicalPoint _releaseButtonCenter;

    /// <summary>The release target's live hit radius in physical px (C2: drawn = hit).</summary>
    [ObservableProperty] private double _releaseRadiusPhysical;

    /// <summary>The release dwell's progress, 0–1, from the detector (C2).</summary>
    [ObservableProperty] private double _releaseProgress;

    /// <summary>Whether the release dwell is held by the pause grace (C2: drawn dimmed).</summary>
    [ObservableProperty] private bool _isReleasePaused;

    /// <summary>The drop target as the overlay draws it (C2).</summary>
    public DragReleaseDisc ReleaseDisc =>
        new(ReleaseButtonCenter, ReleaseRadiusPhysical, ReleaseProgress, IsReleasePaused);

    /// <summary>
    /// C2: the drop target's hit radius in physical px — the larger of an r50 disc
    /// (<see cref="UIConstants.DragDropTargetMinRadius"/>) and the user's fixation radius, capped
    /// at <see cref="UIConstants.DragDropTargetMaxRadius"/>. A rest the user can hold anywhere is
    /// at least their fixation radius wide, so a target smaller than it resets on their ordinary
    /// wander. The fixation radius is the <c>DwellSettings.FixationRadiusPixels</c> baseline,
    /// not <c>GazeEngine.EffectiveFixationRadiusPixels</c>: this view model has no gaze engine,
    /// and the two differ only while adaptation's geometry routing is on — which reaches this
    /// target anyway, as a margin on the registered radius that <see cref="ReleaseRadiusPhysical"/>
    /// reads back live.
    /// </summary>
    public static double DropTargetRadiusPhysical(double fixationRadiusPixels)
    {
        var floor = DpiHelper.LogicalToPhysicalX(UIConstants.DragDropTargetMinRadius);
        var cap = DpiHelper.LogicalToPhysicalX(UIConstants.DragDropTargetMaxRadius);
        return Math.Min(Math.Max(floor, fixationRadiusPixels), cap);
    }

    public DragTrailViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.DragTrail, sink, dwellRegistry, settings.Dwell)
    {
        _settings = settings;

        _settleDetector = new FixationDetector(
            settings.Drag.SettleRadiusPixels,
            settings.Drag.SettleDwellMs);
        _settleDetector.FixationStarted += OnSettleStarted;
        _settleDetector.FixationEnded += OnSettleEnded;
    }

    /// <summary>
    /// Supplies the drag's edge-bar exit hit circles (Cancel and Back), in physical px. The
    /// coordinator sets these per drag from the shared exit anchor; the list is empty by default,
    /// which is exactly the pre-fix behaviour for any caller that does not supply them.
    /// </summary>
    public void SetExitZones(IReadOnlyList<DragExitZone> zones) => _exitZones = zones;

    /// <summary>
    /// Supplies the toolbar Pause button's exit zone as a LIVE query (A5 fixup-02 G2). It is
    /// evaluated on every sample rather than captured at drag start: the toolbar can re-dock or
    /// re-layout mid-drag, be summoned mid-drag (its Pause then becomes dwellable), or hide (a
    /// hidden band's Pause cannot fire, so the spot drops like anywhere else). The provider itself
    /// returns null when Pause cannot fire. Null restores the pre-fix behaviour.
    /// </summary>
    public void SetPauseExitZoneProvider(Func<DragExitZone?>? provider) => _pauseExitZone = provider;

    /// <summary>
    /// Supplies the on-screen rectangle in physical px, plus a small tolerance, for the two
    /// off-screen bands (A5 fixup-03 H1, C6). A sample past the tolerance is the toolbar/Quick
    /// Actions summon gesture and can neither settle nor release the drag; the edge row itself
    /// stays on-screen through <paramref name="tolerancePhysical"/>, so tracker jitter at the
    /// border does not suppress a legitimate drop. C6 splits the past-tolerance side in two,
    /// mirroring C4's repeat gate: up to
    /// <see cref="InteractionManager.RepeatDisarmMarginPhysical"/> past the edge the sample is
    /// WITHHELD (no credit, no reset, no re-centre — the rest survives ordinary hold wander), and
    /// only beyond that margin is it the clean break. The coordinator sets this at drag start
    /// from the primary screen; null (the default) keeps the pre-fix behaviour.
    /// </summary>
    public void SetScreenBounds(PhysicalRect bounds, double tolerancePhysical)
    {
        _screenBounds = bounds;
        _screenTolerancePhysical = tolerancePhysical;
    }

    public void Receive(StartDragTrailMessage message)
    {
        GrabOrigin = message.GrabOrigin;
        CursorPosition = message.GrabOrigin;
        IsDragActive = true;
        DragState = DragSubState.FreeDrag;
        SettleProgress = 0;
        ShowBack = false;
        _inExitZone = false;
        _armAnchor = null;
        _releaseArmed = false;
        _lastEligibleTimestampUs = null;

        ReleaseButtonCenter = message.GrabOrigin;
        SetupDwellTargets();
    }

    public void Receive(DragCancelMessage _)
    {
        IsDragActive = false;
        _settleDetector.Reset();
        DwellDetector.ClearTargets();
        ReleaseProgress = 0;
        DragState = DragSubState.FreeDrag;
    }

    public void Receive(DragDismissManualSelectionMessage _)
    {
        DragState = DragSubState.FreeDrag;
        ShowBack = false;
        SettleProgress = 0;
        _settleDetector.Reset();
        SetupDwellTargets();
    }

    public override void Receive(GazeCursorMovedMessage message)
    {
        if (!IsDragActive) return;

        // A gap-held sample repeats the last valid position, so every write below would be a no-op
        // anyway — but the two detectors would still accumulate against it, completing a release dwell
        // or flipping FreeDrag→Settling on a blink. Skip the whole sample; nothing here is stale as a
        // result, because the position has not changed. The pause term routes through the shared
        // SampleMayDwell gate (round-2 finding 2) so DwellActiveWhilePaused below is actually live.
        if (!SampleMayDwell(message))
        {
            // R2: a gap is a look-away; the samples before it are not this rest's recent past.
            _recentGaze.Clear();
            return;
        }

        CursorPosition = message.Position;
        var position = message.Position;
        var radius = LiveReleaseRadius();

        // R1: the release stays disarmed until the gaze has left the spot it rested on when the
        // drag started (see _armAnchor). No minimum time: that would only postpone the same drop.
        if (!_releaseArmed)
        {
            _armAnchor ??= position;
            var leave = SameSpotRadii * radius;
            if (position.DistanceSquaredTo(_armAnchor.Value) > leave * leave)
                _releaseArmed = true;
        }

        // C6: classify BEFORE the saccade rule, because a withheld sample (past the tolerance,
        // inside C4's disarm margin) may not move or restart the spot at all — not even through
        // StartNewSpot. The band itself is decided by ClassifyScreenBand below.
        var band = ClassifyScreenBand(position);

        // R2: a move of more than one radius within SaccadeWindowSamples samples is a saccade to a
        // new spot, however close that spot is to the old one. Decided BEFORE the detector, so the
        // old spot's dwell can neither complete on this sample nor carry over to the new one.
        if (_lastEligibleTimestampUs is { } last && message.TimestampMicroseconds - last >= HistorySilenceUs)
            _recentGaze.Clear();
        _lastEligibleTimestampUs = message.TimestampMicroseconds;
        if (band != ScreenSampleBand.Withheld)
        {
            foreach (var earlier in _recentGaze)
            {
                if (position.DistanceSquaredTo(earlier) > radius * radius)
                {
                    StartNewSpot(position); // clears _recentGaze
                    break;
                }
            }
        }

        // A rest on an edge-bar drag button belongs to that button's own dwell. Letting the settle
        // or the gaze-following release target accumulate there drops the item before the 600 ms
        // Cancel/Back dwell can fire — the owner's only exit would commit the drag instead.
        if (band == ScreenSampleBand.Exit)
        {
            if (!_inExitZone)
            {
                // PX2/R4: a look at an exit is a clean break — no release progress carries over,
                // and the dwell that restarts after the visit is bounded from where it restarts
                // (fixup-01 R5), not from the spot before the visit.
                _inExitZone = true;
                DwellDetector.ResetAllState();
                RestartSpot(position);
            }

            _settleDetector.Reset();
            SettleProgress = 0;
            if (DragState == DragSubState.Settling)
                DragState = DragSubState.FreeDrag;
            // Filtered to match nothing: the release target holds for the grace, then resets. It
            // never completes while the gaze is on the exit.
            DwellDetector.Update(position, message.TimestampMicroseconds, static _ => false);
        }
        else if (band == ScreenSampleBand.Withheld)
        {
            // C6: the withheld band, mirroring C4's no-fire band. The sample earns no credit —
            // filtered to match nothing, so the release dwell sees it as outside and its normal
            // pause grace holds the progress — and the settle detector is not advanced either (a
            // withheld sample must never help a drop complete off-screen, A5 H1). Nothing is
            // reset: not ResetAllState, not RestartSpot, not the settle. It is also not an exit,
            // so the next exit-zone visit still gets its clean break.
            _inExitZone = false;
            DwellDetector.Update(position, message.TimestampMicroseconds, static _ => false);
        }
        else
        {
            _inExitZone = false;
            if (_releaseArmed)
            {
                // R3: a sample inside the circle as drawn is one the detector credits to this rest.
                // Added BEFORE the update, so the sample that completes the dwell is part of it.
                if (DwellDetector.HitTest(ReleaseTargetId, position))
                {
                    _restSumX += position.X;
                    _restSumY += position.Y;
                    _restCount++;
                }
                DwellDetector.Update(position, message.TimestampMicroseconds);
            }
            else
            {
                // R1: disarmed, filtered to match nothing — exactly as on an exit zone.
                DwellDetector.Update(position, message.TimestampMicroseconds, static _ => false);
            }

            if (DragState == DragSubState.FreeDrag || DragState == DragSubState.Settling)
            {
                _settleDetector.Update(position, message.TimestampMicroseconds);
                SettleProgress = _settleDetector.GetProgress();

                if (SettleProgress > 0 && DragState == DragSubState.FreeDrag)
                    DragState = DragSubState.Settling;
                else if (SettleProgress == 0 && DragState == DragSubState.Settling)
                    DragState = DragSubState.FreeDrag;
            }
        }

        // AFTER the detector: this sample was hit-tested against the circle as drawn, so a sample
        // outside it is never credited — the pause grace holds it — even when the target then
        // re-centres onto it and the next sample, inside, resumes.
        // C6: a withheld sample does NOT follow: the disc belongs to the rest it was on, so one
        // stray sample past the edge cannot move it (and it never enters the rest centroid).
        if (band != ScreenSampleBand.Withheld)
            FollowGaze(position);

        // R2: recorded LAST, after every reset this sample may have made, so the history always
        // holds this sample — the new spot's first one, if the spot was just reset — and nothing
        // from before a reset. C6: a withheld sample is not a dwell-eligible rest sample, so it is
        // neither a saccade source nor a saccade victim; the previous on-screen samples stay the
        // history.
        if (band != ScreenSampleBand.Withheld)
        {
            _recentGaze.Enqueue(position);
            while (_recentGaze.Count > SaccadeWindowSamples)
                _recentGaze.Dequeue();
        }
    }

    /// <summary>
    /// C6: classifies one sample against the screen's two off-screen bands (C4's rule, reused from
    /// <see cref="InteractionManager.RepeatDisarmMarginPhysical"/> so the drag and repeat cannot
    /// drift apart) and the drag's exit zones.
    /// <para>
    /// A sample inside the published bounds plus the tolerance is an ordinary sample. Between the
    /// tolerance and the disarm margin past the edge it is WITHHELD: the A5 H1 summon gesture has
    /// not started, so the rest is not broken, but the sample earns no credit and does not move
    /// the disc (ordinary hold wander on a target hugging an edge lives there). Past the disarm
    /// margin it IS the summon gesture — the clean break A5 H1 pins. With no bounds published
    /// (tests, pre-fix wiring) only the explicit exit zones and the live Pause zone apply, exactly
    /// as before C6.
    /// </para>
    /// </summary>
    private ScreenSampleBand ClassifyScreenBand(PhysicalPoint point)
    {
        var band = ScreenSampleBand.OnScreen;
        if (_screenBounds is { } screen)
        {
            // C6 mirrors C4's Math.Max: a published tolerance wider than the margin leaves no
            // withheld band, because the tolerance is by definition still "on screen".
            var disarm = Math.Max(_screenTolerancePhysical, InteractionManager.RepeatDisarmMarginPhysical);
            if (!IsWithinScreen(point, screen, disarm))
                return ScreenSampleBand.Exit;
            if (!IsWithinScreen(point, screen, _screenTolerancePhysical))
                band = ScreenSampleBand.Withheld;
        }

        // A real exit keeps its clean break even where the withheld band overlaps it (an exit zone
        // could in principle straddle the border; today's zones all sit inside the screen).
        foreach (var zone in _exitZones)
        {
            var radius = zone.RadiusPhysical;
            if (point.DistanceSquaredTo(zone.Center) <= radius * radius)
                return ScreenSampleBand.Exit;
        }

        // LIVE (G2): the toolbar Pause can move, appear or stop being dwellable while the drag
        // runs, so its zone is asked per sample rather than snapshotted at drag start.
        if (_pauseExitZone?.Invoke() is { } pause)
        {
            var radius = pause.RadiusPhysical;
            if (point.DistanceSquaredTo(pause.Center) <= radius * radius)
                return ScreenSampleBand.Exit;
        }
        return band;
    }

    private static bool IsWithinScreen(PhysicalPoint point, PhysicalRect screen, double marginPhysical)
        => point.X >= screen.X - marginPhysical && point.X <= screen.X + screen.Width + marginPhysical
           && point.Y >= screen.Y - marginPhysical && point.Y <= screen.Y + screen.Height + marginPhysical;

    private void OnSettleStarted(PhysicalPoint point)
    {
        if (_settings.SelectionMode == SelectionMode.ManualSelection)
        {
            DragState = DragSubState.ManualSelectionActive;
            ShowBack = true;
        }
        else
        {
            CompleteDrag(point);
        }
    }

    private void OnSettleEnded()
    {
        SettleProgress = 0;
        if (DragState == DragSubState.Settling)
            DragState = DragSubState.FreeDrag;
    }

    /// <summary>
    /// C2: keeps the drop target under the gaze. The target is CENTRED on the gaze it re-centres
    /// onto, which leaves a full radius of room for the wander in every direction; the old
    /// (−14, −14) DIP offset left the cursor 0.2 DIP from the down-right edge, so any drift that
    /// way reset the dwell forever. The owner's below-target resting bias does not argue for an
    /// offset: the hit test runs on the reported gaze, which already contains the bias.
    /// <para>
    /// A gaze outside the circle re-centres it. Within <see cref="SameSpotRadii"/> of the dwell's
    /// spot that is drift, and the dwell is KEPT: the detector already paused it on this outside
    /// sample (the pause grace, uncredited), and the next sample inside the re-centred circle
    /// resumes it. At grace 0 the detector reset it instead, like every other exit at grace 0.
    /// </para>
    /// </summary>
    private void FollowGaze(PhysicalPoint gaze)
    {
        if (!IsDragActive) return;

        // The LIVE hit geometry (adaptation may route a margin onto the registered radius), so the
        // follow decision, the detector's hit test and the drawn disc all agree.
        ReleaseRadiusPhysical = LiveReleaseRadius();
        if (DwellDetector.HitTest(ReleaseTargetId, gaze))
            return;

        ReleaseButtonCenter = gaze;
        DwellDetector.UpdateTargetCenter(ReleaseTargetId, gaze);

        var sameSpot = SameSpotRadii * ReleaseRadiusPhysical;
        if (gaze.DistanceSquaredTo(_releaseSpot) <= sameSpot * sameSpot)
            return;

        // PX2/R4: a release dwell belongs to the SPOT it started at. A1's pause grace may hold a
        // blink or a gap at the same spot, but when the gaze has moved to a new spot the held
        // progress must not follow it — a glance would otherwise drop the item there in a fraction
        // of a dwell.
        RestartSpot(gaze);
        DwellDetector.ResetAllState();
    }

    /// <summary>R2: a saccade lands on a new spot — the target moves there and the dwell restarts.</summary>
    private void StartNewSpot(PhysicalPoint at)
    {
        ReleaseButtonCenter = at;
        DwellDetector.UpdateTargetCenter(ReleaseTargetId, at);
        RestartSpot(at);
        DwellDetector.ResetAllState();
    }

    /// <summary>Re-anchors the same-spot bound at <paramref name="at"/> and starts a new rest centroid (R3).</summary>
    private void RestartSpot(PhysicalPoint at)
    {
        _releaseSpot = at;
        _restSumX = 0;
        _restSumY = 0;
        _restCount = 0;
        _recentGaze.Clear();
    }

    /// <summary>R3: where the rest that completed the dwell was centred; the gaze if none was recorded.</summary>
    private PhysicalPoint RestCentroid() =>
        _restCount > 0 ? new PhysicalPoint(_restSumX / _restCount, _restSumY / _restCount) : CursorPosition;

    private double LiveReleaseRadius()
    {
        foreach (var target in DwellDetector.Targets)
            if (target.Id == ReleaseTargetId)
                return target.Radius;
        return ReleaseRadiusPhysical;
    }

    private void SetupDwellTargets()
    {
        DwellDetector.ClearTargets();
        RestartSpot(ReleaseButtonCenter);
        DwellDetector.AddTarget(new DwellTarget(
            ReleaseTargetId, ReleaseButtonCenter,
            DropTargetRadiusPhysical(_settings.Dwell.FixationRadiusPixels),
            _settings.Dwell.DragReleaseButtonDwellMs));
        ReleaseRadiusPhysical = LiveReleaseRadius();
        ReleaseProgress = 0;
        IsReleasePaused = false;
    }

    // Deliberately dwellable while paused (DwellOverlayViewModelBase.DwellActiveWhilePaused):
    // the drag trail's release/settle button is the gaze way out of a live drag — a safety release must never go inert.
    protected override bool DwellActiveWhilePaused => true;

    protected override void OnTargetActivated(string targetId)
    {
        if (targetId == ReleaseTargetId)
            CompleteDrag(RestCentroid());
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == ReleaseTargetId)
            ReleaseProgress = progress;
    }

    protected override void OnTargetPauseChanged(string targetId, bool paused)
    {
        if (targetId == ReleaseTargetId)
            IsReleasePaused = paused;
    }

    private void CompleteDrag(PhysicalPoint dropPoint)
    {
        IsDragActive = false;
        _settleDetector.Reset();
        DwellDetector.ClearTargets();
        ReleaseProgress = 0;
        DragState = DragSubState.FreeDrag;
        Messenger.Send(new DragDropMessage(dropPoint));
    }

    protected override void OnDispose()
    {
        base.OnDispose();
        _settleDetector.FixationStarted -= OnSettleStarted;
        _settleDetector.FixationEnded -= OnSettleEnded;
    }
}
