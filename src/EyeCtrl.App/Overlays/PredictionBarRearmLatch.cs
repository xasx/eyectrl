namespace EyeCtrl.App.Overlays;

using EyeCtrl.Core;
using EyeCtrl.Core.Keyboard;
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
///
/// <para>
/// F8 gives the latch the same "long look" the keyboard's keys have (F4): while a slot is latched,
/// a SECOND dwell of <see cref="KeyboardLongLook.RepeatDwellUs"/> on it — counted after the bar's
/// pick cooldown, on-slot time only, with a <see cref="KeyboardLongLook.RepeatExcursionToleranceUs"/>
/// excursion pausing instead of resetting — reports the slot as due and the caller picks whatever
/// word it shows NOW. Only a slot with a live dwell target can accumulate: an empty slot (F7's
/// reserved slot 0 before the LM word arrives) cannot fire. The clock starts and the values live in
/// <see cref="KeyboardLongLook"/>, shared with the keys, so one edit tunes both (F9).
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

    /// <summary>
    /// Longest silence between two DELIVERED samples that may still count as continuous on-slot
    /// time toward the long look. Mirrors the detector's own clamp (and the keyboard's
    /// <c>MaxContinuousSampleGapUs</c>): a blink or tracker dropout must not be credited as a
    /// 1.2 s rest and complete the repeat on the first sample after it.
    /// </summary>
    private const long MaxCreditedSampleGapUs = 150 * MathConstants.MicrosecondsPerMillisecond;

    private sealed class SlotState
    {
        /// <summary>The slot's rect when it was latched - the fallback geometry while the
        /// detector has no target for it (after a re-render, or for a now-empty slot).</summary>
        public PhysicalRect? RememberedRect;

        /// <summary>Start of the current continuous off-target run, or null when the last
        /// evaluated sample was still inside (or no sample has been evaluated yet).</summary>
        public long? OffTargetSinceUs;

        /// <summary>The sample timestamp of the pick: the long look's cooldown counts from here.</summary>
        public long LatchedAtUs;

        /// <summary>Credited ON-SLOT time of the long look, in microseconds.</summary>
        public long RepeatDwellUs;

        /// <summary>The last sample credited to the long look's clock.</summary>
        public long RepeatLastInsideUs;

        /// <summary>True while the gaze is on the slot for the long look (the clock is running).</summary>
        public bool RepeatOnTarget;

        /// <summary>Start of the current off-slot excursion, or null while on the slot.</summary>
        public long? RepeatExcursionStartedUs;
    }

    private readonly Dictionary<string, SlotState> _slots = new();

    /// <summary>The stream's last delivered sample, for the silence clamp. 0 = none yet.</summary>
    private long _lastSampleUs;

    internal bool IsLatched(string targetId) => _slots.ContainsKey(targetId);

    /// <summary>
    /// Latches a slot that just fired, remembering its current rect for the samples where the
    /// re-rendered bar has no target for it. Repeated latches restart the leave window AND the long
    /// look: a repeat fire re-arms the slot exactly like a first pick does.
    /// </summary>
    /// <param name="targetId">The slot's target id (<c>pred_N</c>).</param>
    /// <param name="rect">The slot's live rect at activation, or null when it has none.</param>
    /// <param name="timestampUs">The gaze sample that performed the pick; the cooldown and the
    /// long look count from here.</param>
    internal void Latch(string targetId, PhysicalRect? rect, long timestampUs)
        => _slots[targetId] = new SlotState { RememberedRect = rect, LatchedAtUs = timestampUs };

    /// <summary>Clears every latch (hide/re-show and layout reloads must not leave a slot dead).</summary>
    internal void Clear() => _slots.Clear();

    /// <summary>
    /// One gaze frame: advances the leave clock for every latched slot and clears the ones that
    /// have been continuously outside their live hit area (or, while absent, outside the rect
    /// remembered at latch time) for <see cref="LeaveUs"/>. Runs on EVERY sample (keys included) -
    /// gaze on the key grid is outside the bar and must count as a leave. Allocation-free while
    /// nothing is latched.
    /// </summary>
    /// <returns>The slots whose long look completed on this sample, or null when none did. The
    /// caller fires each with the word the slot shows at that moment, then this latch has already
    /// been reset by the caller's re-<see cref="Latch"/>.</returns>
    internal List<string>? Update(PhysicalPoint position, long timestampMicroseconds, DwellDetector detector)
    {
        var sinceLastSampleUs = _lastSampleUs == 0 ? 0 : timestampMicroseconds - _lastSampleUs;
        _lastSampleUs = timestampMicroseconds;

        if (_slots.Count == 0) return null;

        // A silence at the clamp is not evidence the gaze rested: drop the long look's progress
        // rather than credit the whole silence to it (see MaxCreditedSampleGapUs).
        var gapReset = sinceLastSampleUs > MaxCreditedSampleGapUs;
        var cooldownUs = detector.CooldownMs * MathConstants.MicrosecondsPerMillisecond;

        List<string>? due = null;
        List<string>? repeats = null;
        foreach (var (id, slot) in _slots)
        {
            bool inside;
            bool hasTarget = HasTarget(detector, id);
            if (detector.HitTest(id, position))
                inside = true;
            else if (!hasTarget)
                // Absent: the bar re-render has cleared the targets, or the slot is now empty.
                // The remembered rect keeps "resting on the slot" distinct from "left it"; with no
                // remembered geometry there is nothing to judge, so absence is not a leave.
                inside = slot.RememberedRect?.Contains(position) ?? true;
            else
                inside = false;

            // The long look (F8): only a slot with a live target can fire, so an empty slot's
            // remembered rect is for the leave test alone.
            if (gapReset)
                ResetLongLook(slot);
            if (timestampMicroseconds < slot.LatchedAtUs + cooldownUs)
            {
                // The pick cooldown is not dwell time: it neither credits nor starts the clock.
            }
            else if (inside && hasTarget)
            {
                if (!slot.RepeatOnTarget)
                {
                    if (slot.RepeatExcursionStartedUs is { } start
                        && timestampMicroseconds - start > KeyboardLongLook.RepeatExcursionToleranceUs)
                        slot.RepeatDwellUs = 0;
                    slot.RepeatOnTarget = true;
                    slot.RepeatExcursionStartedUs = null;
                    slot.RepeatLastInsideUs = timestampMicroseconds;
                }
                else
                {
                    slot.RepeatDwellUs += timestampMicroseconds - slot.RepeatLastInsideUs;
                    slot.RepeatLastInsideUs = timestampMicroseconds;
                }

                if (slot.RepeatDwellUs >= KeyboardLongLook.RepeatDwellUs)
                    (repeats ??= []).Add(id);
            }
            else if (slot.RepeatOnTarget)
            {
                slot.RepeatOnTarget = false;
                slot.RepeatExcursionStartedUs = timestampMicroseconds;
            }
            else if (slot.RepeatExcursionStartedUs is { } start
                     && timestampMicroseconds - start > KeyboardLongLook.RepeatExcursionToleranceUs)
            {
                slot.RepeatDwellUs = 0;
                slot.RepeatExcursionStartedUs = null;
            }

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

        if (due is not null)
            foreach (var id in due)
                _slots.Remove(id);

        return repeats;
    }

    /// <summary>
    /// The long look's fill for a latched slot (0-1), painted the same way the slot's normal dwell
    /// progress is. 0 for an unlatched slot, and during the pick cooldown.
    /// </summary>
    internal double LongLookProgress(string targetId) =>
        _slots.TryGetValue(targetId, out var slot)
            ? Math.Min(1.0, slot.RepeatDwellUs / (double)KeyboardLongLook.RepeatDwellUs)
            : 0;

    /// <summary>
    /// Whether a latched slot's long look is in flight — the gaze is on it and its clock has begun.
    /// F7's slot-0 fill must treat this as dwell progress in flight ("no fill while a slot has
    /// dwell progress"), so the word cannot appear under a look that is already counting toward a
    /// repeat pick.
    /// </summary>
    internal bool HasLongLookInFlight
    {
        get
        {
            foreach (var slot in _slots.Values)
                if (slot.RepeatOnTarget || slot.RepeatDwellUs > 0)
                    return true;
            return false;
        }
    }

    private static void ResetLongLook(SlotState slot)
    {
        slot.RepeatDwellUs = 0;
        slot.RepeatLastInsideUs = 0;
        slot.RepeatOnTarget = false;
        slot.RepeatExcursionStartedUs = null;
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
