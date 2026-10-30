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
    /// has moved to a new spot and the dwell restarts there (PX2/R4). A drift or wander covers well
    /// under half a radius per sample, while a saccade lands more than a radius away at once.
    /// </summary>
    internal const double SameSpotRadii = 1.5;

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
    /// Supplies the on-screen rectangle in physical px, plus a small tolerance, as an exit zone
    /// (A5 fixup-03 H1). A gaze sample OUTSIDE it is the toolbar/Quick-Actions summon gesture:
    /// letting it settle or release drops the item at an off-screen point, which the OS clamps to
    /// the screen edge (a title bar or tab strip). No settle and no release dwell there; the edge
    /// row itself stays on-screen through <paramref name="tolerancePhysical"/>, so tracker jitter
    /// at the border does not suppress a legitimate drop. The coordinator sets this at drag start
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
        if (!SampleMayDwell(message)) return;

        CursorPosition = message.Position;

        // A rest on an edge-bar drag button belongs to that button's own dwell. Letting the settle
        // or the gaze-following release target accumulate there drops the item before the 600 ms
        // Cancel/Back dwell can fire — the owner's only exit would commit the drag instead.
        if (IsInExitZone(message.Position))
        {
            if (!_inExitZone)
            {
                // PX2/R4: a look at an exit is a clean break — no release progress carries over.
                _inExitZone = true;
                DwellDetector.ResetAllState();
            }

            _settleDetector.Reset();
            SettleProgress = 0;
            if (DragState == DragSubState.Settling)
                DragState = DragSubState.FreeDrag;
            // Filtered to match nothing: the release target holds for the grace, then resets. It
            // never completes while the gaze is on the exit.
            DwellDetector.Update(message.Position, message.TimestampMicroseconds, static _ => false);
        }
        else
        {
            _inExitZone = false;
            DwellDetector.Update(message.Position, message.TimestampMicroseconds);

            if (DragState == DragSubState.FreeDrag || DragState == DragSubState.Settling)
            {
                _settleDetector.Update(message.Position, message.TimestampMicroseconds);
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
        FollowGaze(message.Position);
    }

    private bool IsInExitZone(PhysicalPoint point)
    {
        // H1: past the screen edge is the summon gesture, never a rest to settle or release on.
        if (_screenBounds is { } screen)
        {
            var tol = _screenTolerancePhysical;
            if (point.X < screen.X - tol || point.X > screen.X + screen.Width + tol
                || point.Y < screen.Y - tol || point.Y > screen.Y + screen.Height + tol)
                return true;
        }

        foreach (var zone in _exitZones)
        {
            var radius = zone.RadiusPhysical;
            if (point.DistanceSquaredTo(zone.Center) <= radius * radius)
                return true;
        }

        // LIVE (G2): the toolbar Pause can move, appear or stop being dwellable while the drag
        // runs, so its zone is asked per sample rather than snapshotted at drag start.
        if (_pauseExitZone?.Invoke() is { } pause)
        {
            var radius = pause.RadiusPhysical;
            return point.DistanceSquaredTo(pause.Center) <= radius * radius;
        }
        return false;
    }

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
        _releaseSpot = gaze;
        DwellDetector.ResetAllState();
    }

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
        _releaseSpot = ReleaseButtonCenter;
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
            CompleteDrag(CursorPosition);
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
