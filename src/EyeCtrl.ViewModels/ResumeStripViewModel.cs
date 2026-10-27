namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The paused strip's Resume target (B2a): a dwellable way out of Pause that needs no past-edge
/// summon. In the owner's logs 11 of 28 pauses were never left by gaze, because toolbar Play — the
/// only gaze exit — is reached only by summoning the band.
///
/// <para>
/// <see cref="DwellOverlayViewModelBase.IsVisible"/> is the TARGET's visibility, not the strip's:
/// the strip itself shows for the whole pause (it is the pause feedback the ring relies on), while
/// the target shows only when the strip is shown, the toolbar band is hidden (the band has its own
/// Play — two Resume targets would compete) and the window has placed it.
/// </para>
///
/// <para>
/// <b>Leave-before-arm.</b> Every time the target appears it starts disarmed: a gaze already resting
/// where it appears (the pause began with the eyes there, or the band slid away from under them)
/// must look away for <see cref="ArmLeaveMicroseconds"/> and come back before it can dwell. The same
/// idea as A2's latch; the 150 ms leave is the edge bar's re-arm debounce.
/// </para>
///
/// <para>
/// <b>Firing</b> sends <see cref="PauseToggledMessage"/>, the exact message toolbar Play sends, so a
/// resume runs <c>InteractionManager.TogglePause</c> → FreeLook and nothing else: no click is
/// synthesized, and the engine re-arms the fixation detector on Paused → FreeLook, so the gaze still
/// resting on the target cannot click through. The send is gated on the activating sample having
/// been PAUSED: a toggle from an unpaused state would pause again.
/// </para>
///
/// <para>
/// <b>B2b: the strip's states.</b> The same strip also says when the user has no control for another
/// reason — tracking lost, or no eye tracker — following B1's control status
/// (<see cref="SetControlState"/>, priority NoTracker &gt; TrackingLost &gt; Paused). Only Paused has
/// the Resume target: in the other states <see cref="DwellOverlayViewModelBase.IsVisible"/> stays
/// false, so the disc never shows, arms or fires. The paused input itself still comes only from the
/// strip's own command (InteractionManager, B2a), never from the status.
/// </para>
/// </summary>
public sealed partial class ResumeStripViewModel : DwellOverlayViewModelBase
{
    /// <summary>
    /// The Resume target's dwell id. <c>nameof</c> rather than a quoted id: it is not UI text, and
    /// the literal ratchet rightly treats a quoted word like "Resume" as a text sink.
    /// </summary>
    public const string ResumeTargetId = nameof(ResumeTargetId);

    /// <summary>How much longer than a toolbar button the Resume target always takes, in ms.</summary>
    public const int ToolbarDwellMarginMs = 200;

    /// <summary>
    /// How long the gaze must stay continuously outside the target's hit area before an appearing
    /// target arms. 150 ms — the edge bar's re-arm debounce (the MaxCreditedSampleGapUs precedent).
    /// </summary>
    internal const long ArmLeaveMicroseconds = 150 * MathConstants.MicrosecondsPerMillisecond;

    /// <summary>
    /// The most pause grace the Resume target ever gets, in ms (owner decision, B2a fixup-01: "shorter
    /// grace on Resume"). The disc sits over content for the whole pause, so with A1's full 500 ms
    /// grace short glances that brush it while reading added up to a resume (review 01 F1: 400/300
    /// and 250/250 ms on/off resumed in 2–3 s). 200 ms still forgives a normal blink. Every other
    /// surface, the toolbar band included, keeps the full <see cref="DwellSettings.DwellPauseGraceMs"/>.
    /// </summary>
    public const int MaxPauseGraceMs = 200;

    private readonly DwellSettings _dwell;

    private bool _stripShown;
    private ControlState _controlState = ControlState.Normal;
    private bool _bandVisible = true;
    private PhysicalPoint? _centre;
    private double _radiusPhysical;

    private bool _armed;
    private long? _outsideSinceUs;

    /// <summary>The Resume target's dwell progress, 0..1, for the drawn arc.</summary>
    [ObservableProperty] private double _dwellProgress;

    /// <summary>True while the dwell is grace-held (the gaze stepped off mid-dwell). Display only.</summary>
    [ObservableProperty] private bool _isDwellPaused;

    /// <summary>Which label the strip shows (B2b), by B1's priority; <see cref="ResumeStripLabel.None"/>
    /// when it shows none.</summary>
    [ObservableProperty] private ResumeStripLabel _label;

    /// <summary>Whether the label card is up: always for TrackingLost and NoTracker, and for Paused only
    /// together with the Resume disc (while the band is hidden).</summary>
    [ObservableProperty] private bool _isLabelVisible;

    /// <param name="settings">The live settings: the dwell rule and the (capped) pause grace follow them.</param>
    /// <param name="messenger">The app messenger: gaze in, <see cref="PauseToggledMessage"/> out.</param>
    /// <param name="sink">Struggle telemetry, under the toolbar's context — Resume is toolbar Play's
    /// twin. Deliberately NO <see cref="IDwellDetectorRegistry"/>: the dwell router rewrites a
    /// registered detector's dwell times and hit areas from the adaptation layer, which would undo
    /// the <c>max(ResumeDwellMs, toolbar + 200)</c> floor below.</param>
    public ResumeStripViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink? sink = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.Toolbar, sink,
            dwellRegistry: null, pauseGraceSettings: null)
    {
        // The base is handed NO grace settings on purpose: it would follow the full
        // DwellPauseGraceMs. This surface applies the capped value itself and re-applies it on change.
        _dwell = settings.Dwell;
        DwellDetector.SetPauseGrace(EffectivePauseGraceMs(_dwell));
        _dwell.PropertyChanged += OnDwellSettingChanged;
    }

    /// <summary>
    /// The pause grace the Resume target uses: <c>min(DwellPauseGraceMs, 200)</c>. A grace of 0
    /// (reset-on-leave) stays 0.
    /// </summary>
    public static int EffectivePauseGraceMs(DwellSettings dwell) =>
        Math.Min(dwell.DwellPauseGraceMs, MaxPauseGraceMs);

    /// <summary>
    /// The dwell the Resume target uses: <c>max(ResumeDwellMs, ToolbarButtonDwellMs + 200)</c>, so
    /// Resume is never quicker than the toolbar.
    /// </summary>
    public static int EffectiveDwellMs(DwellSettings dwell) =>
        Math.Max(dwell.ResumeDwellMs, dwell.ToolbarButtonDwellMs + ToolbarDwellMarginMs);

    /// <summary>Whether the target is armed (the leave-before-arm has been satisfied since it appeared).</summary>
    public bool IsArmed => _armed;

    /// <summary>The registered target's centre in physical pixels, or null before placement.</summary>
    public PhysicalPoint? TargetCentre => _centre;

    /// <summary>
    /// The paused state as the strip's own command sees it (true while Paused). Driven from
    /// InteractionManager through the adapter — never from the control status — so the Paused strip
    /// still hides inside the handler that leaves Paused (B2a).
    /// </summary>
    public void SetStripShown(bool shown)
    {
        _stripShown = shown;
        Recompute();
    }

    /// <summary>
    /// B1's committed control state (B2b). TrackingLost and NoTracker take the strip's label over
    /// Paused (B1's priority); Normal and Paused leave it to the paused input.
    /// </summary>
    public void SetControlState(ControlState state)
    {
        _controlState = state;
        Recompute();
    }

    /// <summary>The label a control state and the paused input show, by B1's priority
    /// (NoTracker &gt; TrackingLost &gt; Paused).</summary>
    public static ResumeStripLabel LabelFor(ControlState state, bool paused) => state switch
    {
        ControlState.NoTracker => ResumeStripLabel.NoTracker,
        ControlState.TrackingLost => ResumeStripLabel.TrackingLost,
        _ => paused ? ResumeStripLabel.Paused : ResumeStripLabel.None,
    };

    /// <summary>The toolbar band became visible (true) or hidden (false).</summary>
    public void SetBandVisible(bool visible)
    {
        _bandVisible = visible;
        Recompute();
    }

    /// <summary>
    /// Places the target at <paramref name="centre"/> with <paramref name="radiusPhysical"/>, both in
    /// physical pixels — the circle the window draws. A moved target starts disarmed again.
    /// </summary>
    public void PlaceTarget(PhysicalPoint centre, double radiusPhysical)
    {
        if (_centre == centre && Math.Abs(_radiusPhysical - radiusPhysical) < 1e-6) return;

        _centre = centre;
        _radiusPhysical = radiusPhysical;
        Register();
        Disarm();
        Recompute();
    }

    private void Register()
    {
        if (_centre is not { } centre) return;
        DwellDetector.ClearTargets();
        DwellDetector.AddTarget(new DwellTarget(ResumeTargetId, centre, _radiusPhysical, EffectiveDwellMs(_dwell)));
        ClearProgress();
    }

    private void OnDwellSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(DwellSettings.DwellPauseGraceMs))
            DwellDetector.SetPauseGrace(EffectivePauseGraceMs(_dwell));
        if (e.PropertyName is null or "" or nameof(DwellSettings.ResumeDwellMs)
            or nameof(DwellSettings.ToolbarButtonDwellMs))
            Register();
    }

    private void Recompute()
    {
        var label = LabelFor(_controlState, _stripShown);
        // Only Paused has the Resume target (B2b): another state's label replaces the disc entirely.
        var visible = label == ResumeStripLabel.Paused && !_bandVisible && _centre is not null;
        Label = label;
        IsLabelVisible = label switch
        {
            ResumeStripLabel.None => false,
            ResumeStripLabel.Paused => visible,
            _ => true,
        };
        if (visible == IsVisible) return;

        // Every appearance starts disarmed and every disappearance drops any progress, so a dwell
        // can neither carry over a hide nor start on the sample the target appears under the gaze.
        DwellDetector.ResetAllState();
        ClearProgress();
        Disarm();
        IsVisible = visible;
    }

    private void Disarm()
    {
        _armed = false;
        _outsideSinceUs = null;
    }

    private void ClearProgress()
    {
        DwellProgress = 0;
        IsDwellPaused = false;
    }

    // The strip's whole purpose is to be the way OUT of Pause.
    protected override bool DwellActiveWhilePaused => true;

    /// <summary>
    /// Feeds the detector only once the target is armed. Until then each non-gap sample advances the
    /// leave clock: outside the hit area it runs, inside it restarts; a gap sample restarts it too
    /// (a blink is no evidence of a look away). The detector is not fed while disarmed, so no dwell
    /// can accumulate on a gaze that was already there.
    /// </summary>
    public override void Receive(GazeCursorMovedMessage message)
    {
        NoteSamplePaused(message.IsPaused);

        if (message.IsInGap)
        {
            if (!_armed) _outsideSinceUs = null;
            return;
        }

        if (!IsVisible || !SampleMayDwell(message)) return;

        if (!_armed)
        {
            if (DwellDetector.TargetHit(ResumeTargetId, message.Position))
            {
                _outsideSinceUs = null;
                return;
            }

            _outsideSinceUs ??= message.TimestampMicroseconds;
            if (message.TimestampMicroseconds - _outsideSinceUs.Value < ArmLeaveMicroseconds) return;
            _armed = true;
            _outsideSinceUs = null;
        }

        DwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    protected override void OnTargetActivated(string targetId)
    {
        if (targetId != ResumeTargetId) return;

        // Disarmed first: the target hides with the pause, but if the toggle does not land (an
        // unpaused sample, below) a resting gaze must not re-fire it after the cooldown.
        Disarm();
        ClearProgress();

        // Only from Paused: PauseToggledMessage TOGGLES, so a stale activation after something else
        // resumed (the tray) would pause again. The flag is the activating sample's own state.
        if (!LastSampleWasPaused) return;
        Messenger.Send(new PauseToggledMessage());
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == ResumeTargetId) DwellProgress = progress;
    }

    protected override void OnTargetPauseChanged(string targetId, bool paused)
    {
        if (targetId == ResumeTargetId) IsDwellPaused = paused;
    }

    protected override void OnDispose()
    {
        _dwell.PropertyChanged -= OnDwellSettingChanged;
        base.OnDispose();
    }
}
