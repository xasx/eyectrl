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
///
/// <para>
/// A re-render clears and re-registers the targets, and an empty slot gets no target at all, so a
/// latched slot can be absent for a while. <see cref="Latch"/> therefore remembers the slot's rect
/// at activation time: while the target is absent the gaze is tested against that rect, so a real
/// leave onto the keys still re-arms the slot, while absence under a resting gaze does not.
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

    private sealed class SlotState
    {
        /// <summary>The slot's rect when it was latched - the fallback geometry while the
        /// detector has no target for it (after a re-render, or for a now-empty slot).</summary>
        public PhysicalRect? RememberedRect;

        /// <summary>Start of the current continuous off-target run, or null when the last
        /// evaluated sample was still inside (or no sample has been evaluated yet).</summary>
        public long? OffTargetSinceUs;
    }

    private readonly Dictionary<string, SlotState> _slots = new();

    internal bool IsLatched(string targetId) => _slots.ContainsKey(targetId);

    /// <summary>
    /// Latches a slot that just fired, remembering its current rect for the samples where the
    /// re-rendered bar has no target for it. Repeated latches restart the leave window.
    /// </summary>
    internal void Latch(string targetId, PhysicalRect? rect)
        => _slots[targetId] = new SlotState { RememberedRect = rect };

    /// <summary>Clears every latch (hide/re-show and layout reloads must not leave a slot dead).</summary>
    internal void Clear() => _slots.Clear();

    /// <summary>
    /// One gaze frame: advances the leave clock for every latched slot and clears the ones that
    /// have been continuously outside their live hit area (or, while absent, outside the rect
    /// remembered at latch time) for <see cref="LeaveUs"/>. Runs on EVERY sample (keys included) -
    /// gaze on the key grid is outside the bar and must count as a leave. Allocation-free while
    /// nothing is latched.
    /// </summary>
    internal void Update(PhysicalPoint position, long timestampMicroseconds, DwellDetector detector)
    {
        if (_slots.Count == 0) return;

        List<string>? due = null;
        foreach (var (id, slot) in _slots)
        {
            bool inside;
            if (detector.HitTest(id, position))
                inside = true;
            else if (!HasTarget(detector, id))
                // Absent: the bar re-render has cleared the targets, or the slot is now empty.
                // The remembered rect keeps "resting on the slot" distinct from "left it"; with no
                // remembered geometry there is nothing to judge, so absence is not a leave.
                inside = slot.RememberedRect?.Contains(position) ?? true;
            else
                inside = false;

            if (inside)
            {
                // Back inside: any off-target run so far was not a leave.
                slot.OffTargetSinceUs = null;
                continue;
            }

            if (slot.OffTargetSinceUs is not { } since)
            {
                slot.OffTargetSinceUs = timestampMicroseconds;
                continue;
            }

            if (timestampMicroseconds - since >= LeaveUs)
                (due ??= []).Add(id);
        }

        if (due is null) return;
        foreach (var id in due)
            _slots.Remove(id);
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
