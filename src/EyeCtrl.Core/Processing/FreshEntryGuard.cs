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
/// surface. The caller supplies the live face test (<c>DwellTarget.HitTest</c> on the detector's own
/// target, so adaptation-grown geometry is included), never a recomputed rectangle.
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
    private PhysicalPoint? _anchor;
    private PhysicalPoint? _current;
    private PhysicalPoint? _previous;
    private double _releaseDistancePhysicalSquared;
    private bool _anchorLifted;

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
    }

    /// <summary>Disarms the guard: every target is eligible immediately, exactly as before it existed.</summary>
    public void Disarm()
    {
        IsArmed = false;
        _anchor = null;
        _anchorLifted = false;
        _eligible.Clear();
    }

    /// <summary>
    /// Records one valid (non-gap) sample. Call for every such sample from the surface's stream,
    /// whether or not the surface is currently visible, paused or feeding its detector: the sample
    /// history is what the gates read. Gap-held samples carry no evidence and must not be passed.
    /// </summary>
    public void ObserveValidSample(PhysicalPoint position)
    {
        if (IsArmed && !_anchorLifted && _anchor is { } anchor)
        {
            var dx = position.X - anchor.X;
            var dy = position.Y - anchor.Y;
            _anchorLifted = dx * dx + dy * dy > _releaseDistancePhysicalSquared;
        }

        _previous = _current;
        _current = position;
    }

    /// <summary>
    /// Whether <paramref name="targetId"/> may accumulate dwell from the current sample.
    /// <paramref name="isInsideFace"/> must test the target's LIVE hit area (the detector's own
    /// geometry). A target not yet eligible is added on its entry sample and stays eligible for
    /// the rest of the session; before (A) lifts nothing is eligible at all.
    /// </summary>
    public bool AllowsDwell(string targetId, Func<PhysicalPoint, bool> isInsideFace)
    {
        if (!IsArmed) return true;
        if (!_anchorLifted) return false;
        if (_eligible.Contains(targetId)) return true;
        if (_current is not { } current || !isInsideFace(current)) return false;
        if (_previous is { } previous && isInsideFace(previous)) return false;

        _eligible.Add(targetId);
        return true;
    }
}
