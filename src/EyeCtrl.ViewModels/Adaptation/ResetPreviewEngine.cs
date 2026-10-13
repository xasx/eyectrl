namespace EyeCtrl.ViewModels.Adaptation;

using EyeCtrl.Core.Models;

/// <summary>How a gaze sample must be treated by the adaptation feed while a preview engagement runs.</summary>
public enum PreviewFeed
{
    /// <summary>Process the sample normally — no preview is influencing the gaze.</summary>
    FeedAll,

    /// <summary>Process the sample but exclude it from adaptation statistics — it is inside the
    /// preview's area of influence, so counting it as struggle would poison the learner.</summary>
    FeedExcluded,

    /// <summary>Do not feed the sample at all — the gaze rests on the frozen Reset button or
    /// inside the shown panel.</summary>
    FeedNone
}

/// <summary>How a Reset dwell-progress event must be treated by the toolbar's arc and highlight.</summary>
public enum PreviewProgress
{
    /// <summary>Forward the progress to the target — the arc moves.</summary>
    Propagate,

    /// <summary>Hold the arc at the frozen value — the preview must not advance toward a fire.</summary>
    HoldAtFreeze,

    /// <summary>Drop the arc to zero — the engagement is over and the held arc must fall.</summary>
    Zero
}

/// <summary>
/// Core state machine of the Reset-button adaptation preview (the "seen-latch freeze"): resting on
/// Reset past <see cref="Threshold"/> freezes the dwell arc and shows the preview panel, so reading
/// the preview can never fire the destructive Reset. Only after gaze has been continuously off the
/// preview union for <see cref="LeaveDebounceUs"/> does the engine arm, and only a deliberate
/// re-dwell within <see cref="ArmExpiryUs"/> of arming fires unfrozen.
///
/// The engine generalizes <c>ToolbarViewModel</c>'s pause re-arm latch discipline: all timing is
/// SAMPLE time in microseconds, handed in per call (P4) — the engine has no clock of its own, so it
/// is fully deterministic under test. Event invariant (P18): every field is committed BEFORE any
/// event is raised, so a re-entrant subscriber (Task 5's <see cref="OnBounds"/>) always observes
/// committed state.
/// </summary>
public sealed class ResetPreviewEngine
{
    /// <summary>The toolbar's Reset target id — the engine ignores every other target's freeze logic.</summary>
    public const string ResetId = "ResetAdaptation";

    /// <summary>Dwell progress at which the freeze engages and the preview is requested.</summary>
    public const double Threshold = 0.40;

    /// <summary>Continuous off-union time required to complete a leave run and arm the pass window.</summary>
    public const long LeaveDebounceUs = 120_000;

    /// <summary>How long after arming a gaze return may still fire unfrozen; later returns preview fresh.</summary>
    public const long ArmExpiryUs = 3_000_000;

    private enum EngineState
    {
        Disarmed,
        Frozen,
        Leaving,
        Armed
    }

    private readonly Func<bool>? _isFeedbackSuppressed;

    private EngineState _state = EngineState.Disarmed;

    // Panel bookkeeping. _panelUp means the service was told to show a panel and has not been told
    // to hide it; it gates every visibility-false publish (no hide for a panel that never existed).
    private bool _panelUp;

    // Last known panel bounds from the service (Task 5's OnBounds). Null while unknown — and until
    // Task 5 lands, always null — in which case the union is the hit area alone.
    private PhysicalRect? _panelBounds;

    private int _engagementId;
    private double _held;                    // the frozen arc value; the toolbar mirrors it via HoldAtFreeze
    private long _offSinceUs;                // start of the current continuous off-union run
    private long _armedAtUs;                 // when Leaving completed; the pass window is measured from here
    private bool _passArmed;                 // P3: a deliberate re-dwell is in flight — Propagate, never freeze
    private long _lastSampleUs;              // cached sample time for Task 5's 500 ms unknown-window timeout
    private PhysicalPoint _anchor;
    private bool _frozenSilent;

    /// <summary>
    /// Fires when the panel must show or hide. Arguments: visible, engagement id, the Reset button's
    /// center (the requested anchor — the service owns all placement geometry, P10). Never raised
    /// for a panel that never existed (suppressed at engage, P15/L1).
    /// </summary>
    public event Action<bool, int, PhysicalPoint>? PreviewVisibility;

    /// <summary>Raised whenever <see cref="FrozenSilent"/> flips — Task 6 binds the D18 rim cue to this.</summary>
    public event Action? FrozenSilentChanged;

    /// <summary>True while the leave has completed and a return within <see cref="ArmExpiryUs"/> fires unfrozen.</summary>
    public bool IsArmed => _state == EngineState.Armed;

    /// <summary>True while an engagement is live but no panel is shown — the D18 rim cue condition.</summary>
    public bool FrozenSilent => _frozenSilent;

    /// <summary>
    /// Suppression is the ENGINE's concern (P15, D6 r3): the gate is pull-only — polled at the top
    /// of every <see cref="DecideFeed"/> sample and at engage, never pushed. While a panel is up,
    /// the first suppressed sample is the policy hide; suppression ending mid-engagement restores
    /// nothing (L1).
    /// </summary>
    public ResetPreviewEngine(Func<bool>? isFeedbackSuppressed = null)
    {
        _isFeedbackSuppressed = isFeedbackSuppressed;
    }

    /// <summary>The Reset button's center in physical screen pixels — the anchor of each publish.</summary>
    public void SetAnchor(PhysicalPoint center)
    {
        _anchor = center;
    }

    /// <summary>
    /// Classifies one gaze sample. The union is the hit area plus the known panel rect; while the
    /// rect is unknown (always, until Task 5's OnBounds) the union is the hit area alone, so an
    /// off-button sample both returns <see cref="PreviewFeed.FeedExcluded"/> and starts/continues
    /// the leave run.
    /// </summary>
    public PreviewFeed DecideFeed(bool gazeOverReset, PhysicalPoint position, long timestampUs)
    {
        _lastSampleUs = timestampUs;

        // P15: the pull-only gate, polled at the top of every sample. "Rising edge while a panel
        // is up" — the panel-up guard makes the level check publish exactly once per hide, and no
        // re-show path exists, so a falling edge restores nothing.
        if (_isFeedbackSuppressed is not null
            && _isFeedbackSuppressed()
            && _panelUp
            && _state is EngineState.Frozen or EngineState.Leaving)
        {
            MarkPanelAbsent();
        }

        bool inUnion = gazeOverReset || (_panelBounds is { } rect && rect.Contains(position));

        switch (_state)
        {
            case EngineState.Disarmed:
                return PreviewFeed.FeedAll;

            case EngineState.Frozen:
                if (inUnion)
                {
                    // Over the button or inside the shown panel: held, nothing feeds.
                    return PreviewFeed.FeedNone;
                }

                // Off the union: the leave run starts. Back-in-union samples cancel it (below).
                _state = EngineState.Leaving;
                _offSinceUs = timestampUs;
                return PreviewFeed.FeedExcluded;

            case EngineState.Leaving:
                if (inUnion)
                {
                    // Back in the union: the run so far was a glance, not a leave.
                    _state = EngineState.Frozen;
                    return PreviewFeed.FeedNone;
                }

                if (timestampUs - _offSinceUs >= LeaveDebounceUs)
                {
                    CompleteLeaveAndArm(timestampUs);
                    return PreviewFeed.FeedExcluded;
                }

                return PreviewFeed.FeedExcluded;

            case EngineState.Armed:
                // P3: the pass ends on any off-button sample. The grown overlap can keep
                // HitTest(Reset) true for stray frames, but a real pass rests ON the button.
                if (!gazeOverReset)
                {
                    _passArmed = false;
                }

                return PreviewFeed.FeedAll;

            default:
                return PreviewFeed.FeedAll;
        }
    }

    /// <summary>
    /// Classifies one dwell-progress event for the given target. Only <see cref="ResetId"/> is
    /// stateful: every other target's progress propagates untouched.
    /// </summary>
    public PreviewProgress OnTargetProgress(string targetId, double progress, long timestampUs)
    {
        _lastSampleUs = timestampUs;

        if (targetId != ResetId)
        {
            return PreviewProgress.Propagate;
        }

        // Expiry: a gaze return whose first Reset progress arrives later than ArmExpiryUs after
        // arming disarms FIRST, so this same event previews fresh in the Disarmed handling below.
        // Mid-pass the expiry never applies — a pass that already started legitimately runs to the
        // fire even past 3 s.
        if (_state == EngineState.Armed && !_passArmed && timestampUs - _armedAtUs > ArmExpiryUs)
        {
            _state = EngineState.Disarmed;
        }

        switch (_state)
        {
            case EngineState.Disarmed:
                if (progress >= Threshold)
                {
                    Engage(progress);
                    return PreviewProgress.HoldAtFreeze;   // this event crossed: freeze at it
                }

                return PreviewProgress.Propagate;

            case EngineState.Frozen:
            case EngineState.Leaving:
                // P6: even the leave run's ungated TargetProgress(0) must not drop the arc — if
                // the run cancels back to Frozen, no later event would restore it. Zero is only
                // the post-arm drop.
                return PreviewProgress.HoldAtFreeze;

            case EngineState.Armed:
                if (_passArmed)
                {
                    // Mid-pass: no freeze engages, even past Threshold (P3).
                    return PreviewProgress.Propagate;
                }

                if (progress > 0)
                {
                    // P3: the pass starts on the FIRST progress > 0 within the expiry window — NOT
                    // on HitTest. In the grown-overlap a Modes sample keeps HitTest(Reset) true
                    // while Modes is nearest and fires; a HitTest-latched pass would outlive
                    // everything, and Reset then never emits progress 0 to clear it.
                    _passArmed = true;
                    return PreviewProgress.Propagate;
                }

                return PreviewProgress.Zero;               // the post-arm drop of the held arc

            default:
                return PreviewProgress.Propagate;
        }
    }

    /// <summary>
    /// A target fired. Any activation ends the pass (P3). Reset's own fire is a deliberate
    /// re-dwell, so it additionally clears the latch entirely; another target's activation leaves
    /// the state and <c>_armedAtUs</c> exactly as they were — the latch legitimately persists (L2).
    /// </summary>
    public void OnActivated(string targetId)
    {
        _passArmed = false;

        if (targetId == ResetId)
        {
            Disarm();
        }
    }

    /// <summary>External reset — deactivate, band rebuild, or an absent-report from the service.</summary>
    public void Reset()
    {
        Disarm();
    }

    /// <summary>
    /// Service → VM bounds report for the current engagement (P2). NO-OP STUB in Task 4 — Task 5
    /// implements the three-kind bounds protocol (Rect grows the union; HiddenKeepLast keeps the
    /// last rect in the union until the leave completes; NoPanel falls back to the hit area) and
    /// times the 500 ms unknown window on <see cref="_lastSampleUs"/>. Present now so the P18
    /// re-entrancy contract — a PreviewVisibility subscriber may call this synchronously from
    /// inside the event, against already-committed state — is load-bearing from day one.
    /// </summary>
    public void OnBounds(int engagementId, PhysicalRect? panelBounds)
    {
        // Task 5: match engagementId, resolve PreviewBoundsKind into _panelBounds / MarkPanelAbsent,
        // and time the unknown window against _lastSampleUs (sample time, never wall clock).
    }

    private void Engage(double progress)
    {
        _engagementId++;
        _held = progress;
        _state = EngineState.Frozen;

        bool suppressed = _isFeedbackSuppressed?.Invoke() ?? false;
        bool wasSilent = _frozenSilent;
        _frozenSilent = suppressed;

        if (suppressed)
        {
            // P15/L1: suppressed at engage — the panel never existed. Publish NOTHING (no service
            // is told to show one), the union is the hit area immediately (no unknown window), and
            // the rim cue is the only evidence the freeze happened.
            _panelUp = false;
            if (!wasSilent)
            {
                FrozenSilentChanged?.Invoke();
            }

            return;
        }

        _panelUp = true;
        PreviewVisibility?.Invoke(true, _engagementId, _anchor);   // P18: state committed before the event
    }

    private void CompleteLeaveAndArm(long timestampUs)
    {
        _state = EngineState.Armed;
        _armedAtUs = timestampUs;
        _passArmed = false;
        _held = 0;

        bool panelWasUp = _panelUp;
        _panelUp = false;
        bool wasSilent = _frozenSilent;
        _frozenSilent = false;

        // P18: everything committed, then events. No visibility-false for a panel that never
        // existed or was already policy-hidden.
        if (panelWasUp)
        {
            PreviewVisibility?.Invoke(false, _engagementId, _anchor);
        }

        if (wasSilent)
        {
            FrozenSilentChanged?.Invoke();
        }
    }

    /// <summary>
    /// Internal policy-hide helper (P15): the panel is down for the rest of this engagement. Task 4
    /// treats every absence alike (the union falls back to the hit area); Task 5 refines this into
    /// the three-kind bounds protocol.
    /// </summary>
    private void MarkPanelAbsent()
    {
        bool panelWasUp = _panelUp;
        _panelUp = false;
        bool wasSilent = _frozenSilent;
        _frozenSilent = true;

        // P18: committed, then events.
        if (panelWasUp)
        {
            PreviewVisibility?.Invoke(false, _engagementId, _anchor);
        }

        if (!wasSilent)
        {
            FrozenSilentChanged?.Invoke();
        }
    }

    private void Disarm()
    {
        bool panelWasUp = _panelUp;
        _panelUp = false;
        _state = EngineState.Disarmed;
        _passArmed = false;
        _held = 0;
        bool wasSilent = _frozenSilent;
        _frozenSilent = false;

        // P18: committed, then events.
        if (panelWasUp)
        {
            PreviewVisibility?.Invoke(false, _engagementId, _anchor);
        }

        if (wasSilent)
        {
            FrozenSilentChanged?.Invoke();
        }
    }
}
