namespace EyeCtrl.ViewModels.Adaptation;

using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

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

    /// <summary>
    /// How long the first-show unknown window may last: bounds still unreported this many
    /// microseconds of sample time after engage, while the panel is wanted, cancels the panel and
    /// falls back to the hit-area union (P11) — the reader is never stuck waiting on a silent
    /// service.
    /// </summary>
    public const long BoundsTimeoutUs = 500_000;

    private enum EngineState
    {
        Disarmed,
        Frozen,
        Leaving,
        Armed
    }

    private enum BoundsKind
    {
        /// <summary>Panel wanted, no report yet — the first-show window (guard + timeout).</summary>
        Unknown,

        /// <summary>The live panel rect is known and shapes the union.</summary>
        Rect,

        /// <summary>Policy-hidden: no panel, but the last rect stays in the union (D17).</summary>
        HiddenKeepLast,

        /// <summary>Absent: the union fell back to the hit area; the engagement is closed.</summary>
        NoPanel
    }

    private readonly Func<bool>? _isFeedbackSuppressed;

    private EngineState _state = EngineState.Disarmed;

    // Panel bookkeeping. _panelUp means the service was told to show a panel and has not been told
    // (or told us) it is down; it gates every visibility-false publish (no hide for a panel that
    // never existed) and the mid-read suppression gate.
    private bool _panelUp;

    // Bounds protocol state for the CURRENT engagement (OnBounds, P2). _panelRect is null while
    // unknown — in which case the union is the hit area alone and, while the panel is wanted, the
    // leave run is guarded. Invalidated at Engage and Disarm (D14: first show only — engagement
    // N's rect must never inflate engagement N+1's union).
    private PhysicalRect? _panelRect;
    private BoundsKind _boundsKind;
    private long _unknownSinceUs;             // sample time the unknown window opened (the Engage)

    private int _engagementId;
    private double _held;                    // the frozen arc value; the toolbar mirrors it via HoldAtFreeze
    private long _offSinceUs;                // start of the current continuous off-union run
    private long _armedAtUs;                 // when Leaving completed; the pass window is measured from here
    private bool _passArmed;                 // P3: a deliberate re-dwell is in flight — Propagate, never freeze
    private long _lastSampleUs;              // cached sample clock for the 500 ms unknown-window timeout (P4)
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
    /// Classifies one gaze sample. The union is the hit area plus the panel rect — the live rect
    /// (<see cref="PreviewBoundsKind.Rect"/>) or, after a policy hide
    /// (<see cref="PreviewBoundsKind.HiddenKeepLast"/>, or the mid-read suppression gate), the
    /// last known rect. While the rect is unknown AND the panel is wanted, off-union samples hold
    /// the engagement Frozen with no leave run (the gaze may rest on a panel we cannot see yet)
    /// until <see cref="BoundsTimeoutUs"/> of sample time cancels the panel to the hit-area
    /// fallback. All timing is the handed-in sample timestamp (P4).
    /// </summary>
    public PreviewFeed DecideFeed(bool gazeOverReset, PhysicalPoint position, long timestampUs)
    {
        _lastSampleUs = timestampUs;

        // P15: the pull-only gate, polled at the top of every sample. "Rising edge while a panel
        // is up" — the panel-up guard makes the level check publish exactly once per hide, and no
        // re-show path exists, so a falling edge restores nothing (L1). The hide KEEPS the last
        // known rect in the union (HiddenKeepLast semantics): suppression must not arm the button
        // under the reader's gaze.
        if (_isFeedbackSuppressed is not null
            && _isFeedbackSuppressed()
            && _panelUp
            && _state is EngineState.Frozen or EngineState.Leaving)
        {
            HidePanel(keepLastRect: true, publishCancel: true);
        }

        // Task 5: the first-show unknown window. Bounds still unreported while the panel is wanted
        // and BoundsTimeoutUs of sample time has elapsed → cancel the panel (our show request is
        // retracted) and fall back to the hit-area union; the leave run may start on this very
        // sample (P11 — the reader is never stuck).
        if (_state == EngineState.Frozen
            && _panelUp
            && _boundsKind == BoundsKind.Unknown
            && timestampUs - _unknownSinceUs >= BoundsTimeoutUs)
        {
            HidePanel(keepLastRect: false, publishCancel: true);
        }

        bool inUnion = gazeOverReset
            || (_boundsKind is BoundsKind.Rect or BoundsKind.HiddenKeepLast
                && _panelRect is { } panel
                && panel.Contains(position));

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

                // Task 5 guard: bounds unknown while the panel is wanted — the gaze may be resting
                // on a panel we cannot see yet. Stay Frozen (no leave run); the timeout above or
                // the arriving rect ends the hold.
                if (_panelUp && _boundsKind == BoundsKind.Unknown)
                {
                    return PreviewFeed.FeedExcluded;
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
    /// Service → VM bounds report (P2), resolved per the three-kind protocol:
    /// <see cref="PreviewBoundsKind.Rect"/> stores — or, on a resize, replaces — the live panel
    /// rect and closes the first-show unknown window for good (D14);
    /// <see cref="PreviewBoundsKind.HiddenKeepLast"/> is a policy hide (D17 collision): no panel,
    /// but the last rect STAYS in the union until the leave completes — a hide must not arm the
    /// button under the reader's gaze; <see cref="PreviewBoundsKind.NoPanel"/> falls back to the
    /// hit-area union immediately and closes the engagement to further reports (a late Rect cannot
    /// resurrect a cancelled panel). Reports for any other engagement id, or one that arrives
    /// after the engagement ended, are dropped. P18: may be called synchronously from inside
    /// <see cref="PreviewVisibility"/> against already-committed state.
    /// </summary>
    public void OnBounds(int engagementId, PreviewBoundsKind kind, PhysicalRect? bounds)
    {
        if (engagementId != _engagementId || _state is not (EngineState.Frozen or EngineState.Leaving))
        {
            return;
        }

        switch (kind)
        {
            case PreviewBoundsKind.Rect:
                // First show or resize: replace the last-known rect. Rejected once this
                // engagement's panel is gone (HiddenKeepLast/NoPanel) — L1 has no restore path,
                // so a late report must not resurrect a cancelled panel.
                if (bounds is { } rect && _boundsKind is BoundsKind.Unknown or BoundsKind.Rect)
                {
                    _panelRect = rect;
                    _boundsKind = BoundsKind.Rect;
                }

                break;

            case PreviewBoundsKind.HiddenKeepLast:
                // The service policy-hid the panel itself: no cancel echo (it already knows), but
                // the last rect keeps shaping the union until the leave completes.
                HidePanel(keepLastRect: true, publishCancel: false);
                break;

            case PreviewBoundsKind.NoPanel:
                // Nothing was or is shown: retract our own show request and fall back to the hit
                // area — the leave run may start immediately.
                HidePanel(keepLastRect: false, publishCancel: true);
                break;
        }
    }

    private void Engage(double progress)
    {
        _engagementId++;
        _held = progress;
        _state = EngineState.Frozen;

        // D14 (first show only): bounds are invalidated per engagement — engagement N's stale rect
        // must not inflate engagement N+1's union during its unknown window.
        _panelRect = null;
        _boundsKind = BoundsKind.Unknown;
        _unknownSinceUs = _lastSampleUs;

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
    /// Internal hide helper (P15 + Task 5's bounds protocol): the panel is down for the rest of
    /// this engagement. <paramref name="keepLastRect"/> keeps the last known rect in the union —
    /// policy hides (the mid-read suppression gate, a service D17 collision) must not arm the
    /// button under the reader's gaze; otherwise the union falls back to the hit area and the
    /// engagement is closed to further bounds reports. <paramref name="publishCancel"/> commands
    /// the service to hide (OUR hide: the suppression gate, the NoPanel/timeout cancellation); a
    /// service-initiated hide passes false — no echo for a panel the service already took down.
    /// </summary>
    private void HidePanel(bool keepLastRect, bool publishCancel)
    {
        bool panelWasUp = _panelUp;
        _panelUp = false;
        bool wasSilent = _frozenSilent;
        _frozenSilent = true;

        if (keepLastRect)
        {
            _boundsKind = BoundsKind.HiddenKeepLast;
        }
        else
        {
            _boundsKind = BoundsKind.NoPanel;
            _panelRect = null;
        }

        // P18: committed, then events.
        if (publishCancel && panelWasUp)
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

        // D14: stored bounds die with the engagement — the next one starts from unknown.
        _panelRect = null;
        _boundsKind = BoundsKind.Unknown;

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
