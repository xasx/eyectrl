namespace EyeCtrl.App.Overlays;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Per-slot leave-before-re-arm latch for the keyboard prediction bar.
///
/// <para>
/// A pick re-renders the bar under the resting gaze, so the slot that just fired can hold a new
/// suggestion a moment later. With only the detector's cooldown, a long look on one slot inserts
/// one word per cooldown cycle (e.g. pick "ich" - "habe" now sits under the gaze - pick "habe").
/// The latch holds a fired slot out of dwell until the gaze has been CONTINUOUSLY outside that
/// slot's LIVE hit area for <see cref="LeaveUs"/>, so a single stray sample cannot re-arm it.
/// </para>
///
/// <para>
/// Per slot: other slots stay eligible while one is latched. The latch is state-free toward the
/// detector - the caller excludes latched ids through the detector's per-call target filter, and
/// asks the detector's <see cref="DwellDetector.HitTest"/> for "is the gaze still on this slot?",
/// so adaptation's routed geometry is honoured and no dwell state is perturbed.
/// </para>
/// </summary>
internal sealed class PredictionBarRearmLatch
{
    /// <summary>
    /// How long gaze must be continuously off the fired slot before it can fire again. About four
    /// tracker frames at the Tobii cadence (~33 ms), the same value the toolbar Pause latch uses:
    /// long enough to reject a single outlier, short enough that a deliberate look away re-arms
    /// within a glance.
    /// </summary>
    private const long LeaveUs = 120 * MathConstants.MicrosecondsPerMillisecond;

    // A latched slot and the start of its current continuous off-target run. null = the last
    // sample was still inside (or no sample has been evaluated yet), i.e. no leave has begun.
    private readonly Dictionary<string, long?> _offTargetSinceUs = new();

    internal bool IsLatched(string targetId) => _offTargetSinceUs.ContainsKey(targetId);

    /// <summary>Latches a slot that just fired. Repeated latches restart the leave window.</summary>
    internal void Latch(string targetId) => _offTargetSinceUs[targetId] = null;

    /// <summary>Clears every latch (hide/re-show and layout reloads must not leave a slot dead).</summary>
    internal void Clear() => _offTargetSinceUs.Clear();

    /// <summary>
    /// One gaze frame: advances the leave clock for every latched slot and clears the ones that
    /// have been continuously outside their live hit area for <see cref="LeaveUs"/>. Runs on EVERY
    /// sample (keys included) - gaze on the key grid is outside the bar and must count as a leave.
    /// Allocation-free while nothing is latched.
    /// </summary>
    internal void Update(PhysicalPoint position, long timestampMicroseconds, DwellDetector detector)
    {
        if (_offTargetSinceUs.Count == 0) return;

        List<string>? due = null;
        foreach (var id in _offTargetSinceUs.Keys)
        {
            // Re-rendering the bar clears and re-registers its targets, so there is a window
            // (one sample, by construction) where a latched slot has no target at all. Absence is
            // not a leave: advancing the clock there would re-arm on the frame the slot comes
            // back. Only a registered target can be left.
            if (!HasTarget(detector, id))
                continue;

            if (detector.HitTest(id, position))
            {
                // Back inside: any off-target run so far was not a leave.
                _offTargetSinceUs[id] = null;
                continue;
            }

            if (_offTargetSinceUs[id] is not { } since)
            {
                _offTargetSinceUs[id] = timestampMicroseconds;
                continue;
            }

            if (timestampMicroseconds - since >= LeaveUs)
                (due ??= []).Add(id);
        }

        if (due is null) return;
        foreach (var id in due)
            _offTargetSinceUs.Remove(id);
    }

    private static bool HasTarget(DwellDetector detector, string targetId)
    {
        var targets = detector.Targets;
        for (var i = 0; i < targets.Count; i++)
            if (targets[i].Id == targetId)
                return true;
        return false;
    }
}
