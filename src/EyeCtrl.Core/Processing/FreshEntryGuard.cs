namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// Landing guard for a gaze surface that appears under a resting gaze — the task switcher opening
/// from the toolbar button, and the toolbar re-arming when the switcher closes. The rule was agreed
/// by all four debaters (round-1 synthesis, item 1); it uses raw samples only, no fixation stream:
///
/// <list type="number">
/// <item>
/// <b>(A) distance gate.</b> Nothing is dwell-eligible until a valid sample is more than
/// <see cref="ReleaseDistanceDip"/> from the anchor — the gaze position when the surface appeared
/// (or re-armed). An invalid anchor (no sample, or the last sample was a gap) skips (A).
/// </item>
/// <item>
/// <b>(B) entry gate.</b> After (A) has lifted, a target becomes eligible only on ENTRY: a sample
/// inside its face whose previous VALID sample was outside that face. Once eligible it stays
/// eligible for the rest of the session, so normal dwell, grace and pause-hold behaviour apply
/// unchanged from then on.
/// </item>
/// </list>
///
/// <para>
/// The two gates close different holes and neither replaces the other. (A) is the one that matters
/// when the surface is registered under a gaze that never moved — the switcher draws its tiles after
/// layout, and a re-layout can move a button under a still gaze; (B) is the one that matters when the
/// gaze moves a little, or when the face was already under the gaze at the landing. Without (A), a
/// face that appears/moves under the resting gaze counts as "entered" because the previous sample
/// was outside its new geometry; without (B), a gaze that moves 51 DIP inside the same face would
/// count from that sample.
/// </para>
///
/// <para>
/// State is per session: <see cref="Arm"/> clears eligibility, <see cref="Disarm"/> returns the
/// surface to the old immediate-arm behaviour. <see cref="ObserveValidSample"/> must be called for
/// every non-gap sample, including while the surface is hidden or not feeding its detector: the
/// previous valid sample is what (B) compares against, and the last one before <see cref="Arm"/> is
/// the natural anchor for a surface that is re-armed while already receiving samples.
/// </para>
///
/// <para>
/// Single-threaded by contract, like <see cref="DwellDetector"/>: the UI thread that owns the
/// surface. <see cref="ObserveValidSample"/> receives the detector's own target snapshot and tests
/// each target's LIVE <c>HitTest</c>, so adaptation-grown geometry is included and no caller ever
/// recomputes a rectangle.
/// </para>
/// </summary>
public sealed class FreshEntryGuard
{
    /// <summary>
    /// How far the gaze must move from the anchor before anything may accumulate, in DIP. The
    /// owner's 600 ms toolbar dwell plus a frame is ~0.63 s of resting gaze; 50 DIP is about the
    /// smallest saccade that reads as a deliberate look rather than tracker drift on a button.
    /// Converted to physical pixels at arm time with <see cref="DpiHelper.LogicalToPhysicalX"/>.
    /// </summary>
    public const double ReleaseDistanceDip = 50;

    private readonly HashSet<string> _eligible = new();
    private readonly HashSet<string> _seen = new();
    private readonly Action? _onAnchorLifted;
    private readonly Action<string>? _onTargetEligible;
    private PhysicalPoint? _anchor;
    private PhysicalPoint? _current;
    private PhysicalPoint? _previous;
    private double _releaseDistancePhysicalSquared;
    private bool _anchorLifted;

    /// <summary>
    /// Creates a guard. Both hooks are optional, read-only observations for the session log
    /// (T3 measures <c>guardLiftMs</c> and <c>liftToEntryMs</c> from them); neither can change
    /// the rule, and the guard behaves exactly as it did without them.
    /// <paramref name="onAnchorLifted"/> runs once, on the sample that lifts (A) — never for an
    /// invalid anchor, which starts with the gate already open (and is a "no lift" for the log's
    /// purposes). <paramref name="onTargetEligible"/> runs once per target, with its id, on the
    /// sample that makes it eligible. Both are invoked synchronously inside
    /// <see cref="ObserveValidSample"/>, on the surface's thread; a hook must be cheap and must
    /// not throw — the caller is the gaze path.
    /// </summary>
    public FreshEntryGuard(Action? onAnchorLifted = null, Action<string>? onTargetEligible = null)
    {
        _onAnchorLifted = onAnchorLifted;
        _onTargetEligible = onTargetEligible;
    }

    /// <summary>Whether the guard currently gates anything. A disarmed guard allows everything.</summary>
    public bool IsArmed { get; private set; }

    /// <summary>
    /// Arms the guard for one session. <paramref name="anchor"/> is the gaze at the moment the
    /// surface appeared (or re-armed); <c>null</c> means invalid — no sample, or the last sample
    /// was a gap — and skips (A), leaving only the entry gate.
    /// </summary>
    public void Arm(PhysicalPoint? anchor)
    {
        IsArmed = true;
        _anchor = anchor;
        _anchorLifted = anchor is null;
        var releaseDistancePhysical = DpiHelper.LogicalToPhysicalX(ReleaseDistanceDip);
        _releaseDistancePhysicalSquared = releaseDistancePhysical * releaseDistancePhysical;
        _eligible.Clear();
        _seen.Clear();
    }

    /// <summary>Disarms the guard: every target is eligible immediately, exactly as before it existed.</summary>
    public void Disarm()
    {
        IsArmed = false;
        _anchor = null;
        _anchorLifted = false;
        _eligible.Clear();
        _seen.Clear();
    }

    /// <summary>
    /// Records one valid (non-gap) sample and evaluates entries eagerly for every registered
    /// target. Call for every such sample from the surface's stream, whether or not the surface is
    /// currently visible, paused or feeding its detector: the sample history is what the gates
    /// read. Gap-held samples carry no evidence and must not be passed.
    /// <para>
    /// <b>Why eager.</b> Entries used to be decided lazily inside <c>DwellDetector.Update</c>'s
    /// filter. The filter runs only on samples that reach
    /// <c>FindNearestHittingTargetId</c> — the detector's 400 ms post-activation cooldown, the
    /// toolbar's <c>FeedNone</c> path, the pause re-arm latch and the exercise-screen hold all skip
    /// it. Because the previous/current history advances on every sample anyway, an entry landing
    /// in one of those windows was lost for good and the target stayed dead until the gaze left and
    /// returned (the 2026-08-17 "no feedback" failure class). Evaluating here, once per valid
    /// sample and before any gate, makes the entry independent of whether the detector consumes the
    /// sample. Cost: one <c>HitTest</c> per target per sample, and only while armed.
    /// </para>
    /// <para>
    /// <b>First sight.</b> A target often appears AFTER the gaze already landed on it: the switcher
    /// registers its tiles at Loaded priority, so an anticipatory look (or a slow first layout)
    /// puts the previous valid sample inside the future face before the target exists. The first
    /// time an id appears in <paramref name="targets"/> this session, if (A) has lifted and the gaze
    /// is inside, the entry is judged against the ANCHOR instead of the previous sample: an anchor
    /// outside the face licenses the entry, an anchor inside it (the landing tile always contains
    /// it) does not. A null anchor keeps the normal previous-sample rule.
    /// </para>
    /// </summary>
    public void ObserveValidSample(PhysicalPoint position, IReadOnlyList<DwellTarget> targets)
    {
        if (IsArmed && !_anchorLifted && _anchor is { } anchor)
        {
            var dx = position.X - anchor.X;
            var dy = position.Y - anchor.Y;
            if (dx * dx + dy * dy > _releaseDistancePhysicalSquared)
            {
                _anchorLifted = true;
                _onAnchorLifted?.Invoke();
            }
        }

        _previous = _current;
        _current = position;

        if (!IsArmed) return;

        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            // Mark every target seen, even while (A) is unlifted or the gaze is elsewhere: first
            // sight is a one-time licence, so a target that existed before the release must never
            // gain it afterwards. That is what keeps the landing tile guarded.
            //
            // Coordinators' decision (fixup-02 R2): the agreed rule is literal — an entry only
            // counts AFTER (A) has lifted. So an anchor in the inter-tile gap followed by a drift
            // of more than 50 DIP into the neighbouring tile, without ever leaving its face, leaves
            // that tile inert until the gaze leaves and re-enters. That is the safer direction and
            // is practically unreachable once the T2 keep-out layout moves the toolbar button off
            // the tile area; pinned by DriftIntoATilePastTheReleaseDistance_WithoutLeavingIt.
            var firstSight = _seen.Add(target.Id);
            if (!_anchorLifted || _eligible.Contains(target.Id) || !target.HitTest(position))
                continue;

            var entered = firstSight && _anchor is { } landing
                ? !target.HitTest(landing)
                : _previous is not { } previous || !target.HitTest(previous);

            if (entered)
            {
                _eligible.Add(target.Id);
                _onTargetEligible?.Invoke(target.Id);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="targetId"/> may accumulate dwell from the current sample. A pure
    /// lookup: entries were already decided by <see cref="ObserveValidSample"/>, so a target not in
    /// the eligible set is simply not entered yet. Disarmed allows everything.
    /// </summary>
    public bool AllowsDwell(string targetId) => !IsArmed || _eligible.Contains(targetId);
}
