namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Adaptation;
using System.Collections.Concurrent;

/// <summary>
/// Dwell detector for gaze-based interactions.
/// IMPORTANT: This class is designed for single-threaded use. All public methods must be called
/// from the same thread (typically the UI thread via Dispatcher). The GazeEngineAdapter ensures
/// this by marshaling all gaze events to the UI thread using Dispatcher.BeginInvoke().
/// </summary>
internal class Rv4cOldDwellDetector
{
    /// <summary>
    /// Longest silence between two DELIVERED samples that may still be credited as continuous
    /// dwell. Every caller skips gap-held samples before this detector (the shared
    /// <c>DwellOverlayViewModelBase.SampleMayDwell</c> gate and its overrides; the keyboard's
    /// coordinator similarly withholds them), so a blink arrives here as a silence — and a
    /// timestamp-based clock would credit the whole silence to the first real sample after it.
    /// A silence at or beyond this bound re-anchors accumulated dwell to the current sample
    /// (restart); there is no cap, so a shorter silence resumes exactly where it left off.
    ///
    /// <para>
    /// 150 ms is <c>CursorSettings.MaxGapInterpolationMs</c>'s default — the engine's own boundary
    /// for "too long to hold": <c>GapInterpolator</c> breaks the sequence at
    /// <c>gap &gt;= maxGap</c>. Using the same bound and the same <c>&gt;=</c> comparison means a
    /// silence the engine refuses to hold always restarts a dwell. That is deliberately the
    /// engine's boundary rather than a multiple of a sample rate: the detector sees samples after
    /// dispatcher marshaling, not the tracker's cadence, so a cadence-derived multiplier would be
    /// reasoning about the wrong clock. It is a fixed constant, not the setting — the setting is
    /// user-tunable to 500 ms, past every standard dwell, so it cannot be the dwell safety bound.
    /// Deliberately one-directional: raising <c>MaxGapInterpolationMs</c> makes the engine hold
    /// longer silences, but this bound does not follow it — the setting is a cursor-smoothing
    /// knob, and stricter-than-the-engine is the only safe direction.
    /// </para>
    ///
    /// <para>
    /// Over-clamping is app-wide (toolbar, action menu, settings, emoji tabs all ride this
    /// detector), which is why the bound is generous rather than tight, and why the sub-bound
    /// resume behaviour is pinned by its own row. These dwells are 600–700 ms, so 150 ms is a
    /// fifth of the window.
    /// </para>
    ///
    /// <para>
    /// The bound is below EVERY configured minimum dwell: the keyboard family's floor is
    /// <c>AdaptationDwellRanges.KeyboardMinDwellMs</c> = 300 (prediction bar, emoji category tabs)
    /// and the standard surfaces' is <c>StandardMinDwellMs</c> = 200, so a SINGLE credited silence
    /// cannot complete a dwell on its own. Note the comparison operator asymmetry with the key grid:
    /// this detector restarts on <c>&gt;=</c> this bound (exactly 150 ms is a restart), while
    /// <c>KeyboardViewModel.MaxContinuousSampleGapUs</c> credits exactly 150 ms and restarts only
    /// above it — tests must speak for the path whose operator they model.
    /// </para>
    ///
    /// <para>
    /// The clamp is PER GAP, not cumulative: N credited silences contribute up to 150·N, so a dwell
    /// of D can still be assembled from <c>ceil(D/150)</c> stalls — two at the 300 ms floor, three
    /// at the shipped 450 ms default. That multi-stall path is open at every floor and was already
    /// open before the floor existed; it is accepted because a DELIVERED sample must intervene
    /// between stalls (a sample between two silences means gaze was reported on the key, and a real
    /// blink is one long gap, not a series of short ones), whereas the single-stall case needs no
    /// reported gaze at all. Do not restate the claim as "no stall can complete a dwell" — the exact
    /// property is the single one above.
    /// </para>
    /// </summary>
    private const long MaxCreditedSampleGapUs = 150 * MathConstants.MicrosecondsPerMillisecond;

    private readonly ConcurrentDictionary<string, DwellTarget> _targets = new();

    // Copy-on-write snapshot of _targets for allocation-free, thread-safe enumeration on the
    // per-sample Update hot path. Rebuilt on add/remove/clear; read locklessly via a volatile load.
    // The array is never mutated in place, so concurrent mutation can never throw mid-enumeration.
    private volatile DwellTarget[] _targetsSnapshot = Array.Empty<DwellTarget>();
    private long _cooldownMicroseconds;
    private long _lastTimestamp;
    private long? _lastActivationTimestamp;

    private readonly DwellContext _context;
    private readonly IStruggleTelemetrySink? _sink;
    private Func<int, int>? _effectiveDwellMap;
    private Func<double, double>? _effectiveGeometryMap;

    /// <summary>
    /// Initializes a new instance of the <see cref="Rv4cOldDwellDetector"/> class.
    /// </summary>
    /// <param name="cooldownMs">Cooldown period in milliseconds after an activation before another can trigger.</param>
    /// <param name="context">Interaction surface this detector serves; tags emitted outcomes.</param>
    /// <param name="sink">Optional telemetry sink. When null, no outcomes are emitted and the
    /// hot path is byte-for-byte identical to a detector with no telemetry.</param>
    public Rv4cOldDwellDetector(int cooldownMs = 0, DwellContext context = DwellContext.Other,
        IStruggleTelemetrySink? sink = null)
    {
        _cooldownMicroseconds = cooldownMs * MathConstants.MicrosecondsPerMillisecond;
        _context = context;
        _sink = sink;
    }

    /// <summary>Raised when a target's dwell time is met.</summary>
    public event Action<string>? TargetActivated;
    /// <summary>Raised with the target's dwell progress (0.0–1.0) each update cycle.</summary>
    public event Action<string, double>? TargetProgress;

    /// <summary>Sets the cooldown period between activations.</summary>
    /// <param name="cooldownMs">Cooldown duration in milliseconds.</param>
    public void SetCooldown(int cooldownMs)
    {
        _cooldownMicroseconds = cooldownMs * MathConstants.MicrosecondsPerMillisecond;
    }

    /// <summary>Currently configured cooldown in milliseconds — the exact inverse of
    /// <see cref="SetCooldown"/>, so callers can observe what was last applied.</summary>
    public int CooldownMs => (int)(_cooldownMicroseconds / MathConstants.MicrosecondsPerMillisecond);

    /// <summary>Adds a target for dwell detection. Replaces any existing target with the same ID.</summary>
    public void AddTarget(DwellTarget target)
    {
        _targets[target.Id] = target;
        _targetsSnapshot = _targets.Values.ToArray();
        ApplyMap(target);
        ApplyGeometryMap(target);
    }
    /// <summary>Removes a target from dwell detection.</summary>
    public void RemoveTarget(string id)
    {
        if (_targets.TryRemove(id, out _))
            _targetsSnapshot = _targets.Values.ToArray();
    }
    /// <summary>Removes all targets from dwell detection.</summary>
    public void ClearTargets()
    {
        _targets.Clear();
        _targetsSnapshot = _targets.Values.ToArray();
    }

    /// <summary>
    /// Clears the post-activation cooldown so the next <see cref="Update(PhysicalPoint, long)"/> can accumulate dwell
    /// immediately. Use when repurposing a shared detector for a new interaction context that must
    /// not inherit the previous context's cooldown (e.g. the unified edge-button bar).
    /// </summary>
    public void ResetCooldown() => _lastActivationTimestamp = null;

    /// <summary>
    /// Resets accumulated dwell state on all targets without removing them.
    /// Call this when switching input contexts (e.g. a modal dialog opening or closing)
    /// to prevent a stale <c>EnteredTimestamp</c> from causing a spurious immediate
    /// activation when Update resumes after a pause.
    /// </summary>
    public void ResetAllState()
    {
        var targets = _targetsSnapshot;
        for (int i = 0; i < targets.Length; i++)
        {
            var t = targets[i];
            if (t.IsGazeInside)
                TargetProgress?.Invoke(t.Id, 0);
            t.IsGazeInside = false;
            t.EnteredTimestamp = null;
        }
    }

    /// <summary>
    /// Update a target's center position without resetting dwell state.
    /// Used by toolbar/overlays that need to track moving screen positions.
    /// </summary>
    /// <param name="id">Target ID</param>
    /// <param name="newCenter">New center position in physical screen pixels</param>
    public void UpdateTargetCenter(string id, PhysicalPoint newCenter)
    {
        if (_targets.TryGetValue(id, out var target))
            target.Center = newCenter;
    }

    /// <summary>
    /// Grows a target's hit area by <paramref name="marginPx"/> physical pixels on every side,
    /// relative to its as-registered extents, without resetting accumulated dwell state. Works for
    /// both circular and rectangular targets (a rect's bounds are outset — adjusting only the radius
    /// would be a silent no-op there, since <see cref="DwellTarget.HitTest"/> ignores the radius when
    /// bounds are set). Enlarging mid-dwell keeps progress (gaze already inside stays inside);
    /// shrinking such that gaze falls outside ends the dwell on the next
    /// <see cref="Update(PhysicalPoint, long)"/> as a normal gaze-left (design C4). Re-applying the
    /// same margin is idempotent, so a controller may call this every tick.
    /// </summary>
    /// <param name="id">Target ID.</param>
    /// <param name="marginPx">Signed margin in physical pixels; 0 restores the as-registered geometry.</param>
    public void UpdateTargetGeometry(string id, double marginPx)
    {
        if (_targets.TryGetValue(id, out var target))
            target.ApplyHitAreaMargin(marginPx);
    }

    /// <summary>
    /// Stores the effective-dwell-time map (adaptation routing) and applies it to every current
    /// target; targets added afterwards are born effective (spec §5). The map receives each target's
    /// as-registered baseline ms (never the current effective value), so re-applying is idempotent.
    /// <c>null</c> restores every target to its exact baseline. State-preserving: never touches
    /// <c>EnteredTimestamp</c>/<c>IsGazeInside</c> — the next <see cref="Update(PhysicalPoint, long)"/>
    /// re-evaluates elapsed against the new threshold. Same threading contract as the other mutators
    /// (single thread — the UI thread, or the owning service's lock).
    /// </summary>
    public void SetEffectiveDwellTime(Func<int, int>? mapBaselineMsToEffectiveMs)
    {
        _effectiveDwellMap = mapBaselineMsToEffectiveMs;
        var targets = _targetsSnapshot;
        for (int i = 0; i < targets.Length; i++)
            ApplyMap(targets[i]);
    }

    private void ApplyMap(DwellTarget target)
    {
        // Null map = restore: the baseline IS the null case's effective value. A bare early-return
        // here would leave targets stuck at the last effective value (review round 1, blocker 1).
        var map = _effectiveDwellMap;
        target.ApplyEffectiveDwellMs(map is null ? target.BaseDwellMs : map(target.BaseDwellMs));
    }

    /// <summary>
    /// Stores the effective-geometry map (adaptation routing) and applies it to every current
    /// target; targets added afterwards are born effective (spec §5). The map receives each target's
    /// base hit extent (<see cref="DwellTarget.BaseHitExtent"/> — radius, or the smaller rect
    /// half-extent) and returns an ABSOLUTE extent; the applied margin is
    /// <c>map(base) − base</c> (the §13.3 unit-mismatch resolution), so re-applying is idempotent.
    /// <c>null</c> restores every target's as-registered geometry (margin 0). State-preserving: never
    /// touches <c>EnteredTimestamp</c>/<c>IsGazeInside</c> — enlarging keeps an in-progress dwell
    /// alive; a shrink that leaves the gaze outside ends it on the next
    /// <see cref="Update(PhysicalPoint, long)"/> as a normal gaze-left (design §7.3/C4). Same
    /// threading contract as the other mutators (single thread — the UI thread, or the owning
    /// service's lock).
    /// </summary>
    public void SetEffectiveGeometry(Func<double, double>? mapBaseExtentToEffectiveExtent)
    {
        _effectiveGeometryMap = mapBaseExtentToEffectiveExtent;
        var targets = _targetsSnapshot;
        for (int i = 0; i < targets.Length; i++)
            ApplyGeometryMap(targets[i]);
    }

    private void ApplyGeometryMap(DwellTarget target)
    {
        // Null map = restore: margin 0 IS the null case's effective geometry. A bare early-return
        // here would leave targets stuck at the last margin (the SetEffectiveDwellTime lesson,
        // dwell plan review round 1 blocker 1).
        var map = _effectiveGeometryMap;
        double extent = target.BaseHitExtent;
        target.ApplyHitAreaMargin((map is null ? extent : map(extent)) - extent);
    }

    /// <summary>
    /// Rebases a rect target's as-registered extents to <paramref name="rect"/> and re-applies the
    /// stored effective-geometry map, so an owner that moves/resizes a live target in place
    /// (ExternalDwellService's UpdateMessage) neither loses the routed margin nor has a later map
    /// application revert the new size to the stale base extents. With no map stored this is
    /// behaviour-identical to a raw Bounds/Center write (margin 0 recomputes the exact rect).
    /// State-preserving; rect targets only (circular targets have no external geometry source).
    /// Same threading contract as the other mutators (single thread — the UI thread, or the owning
    /// service's lock).
    /// </summary>
    public void RebaseTarget(string id, PhysicalRect rect)
    {
        if (_targets.TryGetValue(id, out var target))
        {
            target.Rebase(rect);
            ApplyGeometryMap(target);
        }
    }

    /// <summary>
    /// Updates the dwell detector with the current gaze position and timestamp.
    /// Must be called from the UI thread (enforced by GazeEngineAdapter's Dispatcher marshaling).
    /// </summary>
    /// <param name="gazePoint">Current gaze position in physical screen pixels</param>
    /// <param name="timestampMicroseconds">Timestamp in microseconds</param>
    public void Update(PhysicalPoint gazePoint, long timestampMicroseconds)
        => Update(gazePoint, timestampMicroseconds, targetFilter: null);

    /// <summary>
    /// Updates the dwell detector with the current gaze position, timestamp, and an optional
    /// per-call target filter. Targets excluded by the filter are never considered as nearest
    /// candidate and cannot accumulate dwell.
    /// </summary>
    /// <param name="gazePoint">Current gaze position in physical screen pixels</param>
    /// <param name="timestampMicroseconds">Timestamp in microseconds</param>
    /// <param name="targetFilter">Optional filter: only targets for which this returns true are eligible.
    /// A null filter means all targets are eligible (byte-for-byte identical to the 2-arg overload).</param>
    public void Update(PhysicalPoint gazePoint, long timestampMicroseconds,
        Func<DwellTarget, bool>? targetFilter)
    {
        // Silence since the last DELIVERED sample, measured before this stamp replaces it. Every
        // Update is a delivered sample — including cooldown, filtered and non-nearest passes — so
        // this is the stream's cadence, not any target's state.
        long sinceLastSampleUs = timestampMicroseconds - _lastTimestamp;
        _lastTimestamp = timestampMicroseconds;

        // Read the copy-on-write snapshot once: a stable, immutable view for this call. Iterating it
        // allocates nothing and never throws under concurrent add/remove (the array is never mutated).
        var targets = _targetsSnapshot;

        // A silence at the bound is not held gaze. Re-anchor accumulated dwell to now BEFORE any
        // path reads it, so the activation computation below and the abandon telemetry further
        // down both consume bounded state instead of each patching its own arithmetic — and so the
        // case where the first post-gap sample already leaves the target is covered too (a
        // nearest-arm-only check would miss it). At most one target holds a timestamp (the code
        // below clears every non-nearest one), so this is a single-target operation in practice.
        // The continuation arm emits the resulting 0 progress; the abandon arm already does.
        if (sinceLastSampleUs >= MaxCreditedSampleGapUs)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i].EnteredTimestamp.HasValue)
                    targets[i].EnteredTimestamp = timestampMicroseconds;
            }
        }

        // Cooldown: skip all dwell accumulation if within cooldown period
        if (_lastActivationTimestamp.HasValue &&
            (timestampMicroseconds - _lastActivationTimestamp.Value) < _cooldownMicroseconds)
        {
            // Reset all targets during cooldown (prevent partial accumulation)
            for (int i = 0; i < targets.Length; i++)
            {
                var t = targets[i];
                if (t.IsGazeInside)
                    TargetProgress?.Invoke(t.Id, 0);
                t.IsGazeInside = false;
                t.EnteredTimestamp = null;
            }
            return;
        }
        _lastActivationTimestamp = null; // cooldown elapsed, clear it

        // ── Find the single nearest target within hit range ──
        var nearestId = FindNearestHittingTargetId(targets, gazePoint, targetFilter);

        // ── Accumulate dwell on nearest only; reset all others ──
        string? activatedId = null;
        // One slot each is sufficient: only the nearest target is ever given IsGazeInside == true, and
        // every other target is cleared in the same pass, so at most one target can transition out of
        // "inside" per Update. If that invariant is ever relaxed, these must become collections or
        // abandonments will be silently overwritten.
        DwellOutcome? activationOutcome = null;
        DwellOutcome? abandonOutcome = null;

        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            bool isNearest = target.Id == nearestId;

            if (isNearest)
            {
                if (!target.IsGazeInside)
                {
                    target.IsGazeInside = true;
                    target.EnteredTimestamp = timestampMicroseconds;
                }
                else if (target.EnteredTimestamp.HasValue)
                {
                    long elapsed = timestampMicroseconds - target.EnteredTimestamp.Value;
                    double progress = Math.Min(1.0, (double)elapsed / target.DwellTimeMicroseconds);
                    TargetProgress?.Invoke(target.Id, progress);

                    if (elapsed >= target.DwellTimeMicroseconds)
                    {
                        activatedId = target.Id;
                        if (_sink is not null)
                            activationOutcome = new DwellOutcome(
                                timestampMicroseconds, _context, target.Id,
                                OutcomeKind.Activated, AbandonReason.None,
                                1.0, gazePoint, target.Center, target.Radius, target.Bounds);
                        target.EnteredTimestamp = null;
                        target.IsGazeInside = false;
                        _lastActivationTimestamp = timestampMicroseconds;
                    }
                }
            }
            else
            {
                if (target.IsGazeInside)
                {
                    TargetProgress?.Invoke(target.Id, 0);
                    if (_sink is not null && target.EnteredTimestamp.HasValue
                        && (targetFilter is null || targetFilter(target)))
                    {
                        long abandonElapsed = timestampMicroseconds - target.EnteredTimestamp.Value;
                        double abandonProgress = Math.Min(1.0,
                            (double)abandonElapsed / target.DwellTimeMicroseconds);
                        if (abandonProgress >= TelemetryThresholds.AbandonProgress)
                            abandonOutcome = new DwellOutcome(
                                timestampMicroseconds, _context, target.Id,
                                OutcomeKind.Abandoned,
                                nearestId is null ? AbandonReason.GazeLeft : AbandonReason.NearestChanged,
                                abandonProgress, gazePoint, target.Center, target.Radius, target.Bounds);
                    }
                }
                target.IsGazeInside = false;
                target.EnteredTimestamp = null;
            }
        }

        // Fire activation after loop — handlers may modify the target collection
        if (activatedId != null)
        {
            TargetProgress?.Invoke(activatedId, 0);
            TargetActivated?.Invoke(activatedId);
        }

        if (_sink is not null)
        {
            if (abandonOutcome is { } abandoned)
                _sink.Report(abandoned);
            if (activationOutcome is { } completed)
                _sink.Report(completed);
        }
    }

    /// <summary>
    /// The id of the nearest hitting target for <paramref name="gazePoint"/>, or null when no
    /// eligible target's region contains the point. This is the SAME selection <see cref="Update(PhysicalPoint, long)"/>
    /// uses to award dwell — callers that need to predict it (the mod-bar re-arm gate) must come
    /// here rather than reimplementing the scan.
    /// </summary>
    public string? NearestHittingTargetId(PhysicalPoint gazePoint, Func<DwellTarget, bool>? targetFilter = null)
        => FindNearestHittingTargetId(_targetsSnapshot, gazePoint, targetFilter);

    /// <summary>
    /// Whether the CURRENT effective region of target <paramref name="targetId"/> contains
    /// <paramref name="gazePoint"/>. Unknown ids answer false.
    /// </summary>
    public bool TargetHit(string targetId, PhysicalPoint gazePoint)
    {
        foreach (var t in _targetsSnapshot)
            if (t.Id == targetId && t.HitTest(gazePoint))
                return true;
        return false;
    }

    private static string? FindNearestHittingTargetId(DwellTarget[] targets, PhysicalPoint gazePoint, Func<DwellTarget, bool>? targetFilter)
    {
        string? nearestId = null;
        double nearestDistSq = double.MaxValue;

        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            if (targetFilter != null && !targetFilter(target))
                continue;
            if (target.HitTest(gazePoint))
            {
                double dx = gazePoint.X - target.Center.X;
                double dy = gazePoint.Y - target.Center.Y;
                double distSq = dx * dx + dy * dy;
                if (distSq < nearestDistSq)
                {
                    nearestDistSq = distSq;
                    nearestId = target.Id;
                }
            }
        }

        return nearestId;
    }

    /// <summary>
    /// Gets the current progress (0.0 to 1.0) for a target.
    /// </summary>
    public double GetProgress(string targetId)
    {
        if (!_targets.TryGetValue(targetId, out var target)) return 0;
        if (!target.IsGazeInside || !target.EnteredTimestamp.HasValue) return 0;
        long elapsed = _lastTimestamp - target.EnteredTimestamp.Value;
        return Math.Min(1.0, (double)elapsed / target.DwellTimeMicroseconds);
    }

    /// <summary>
    /// Whether a registered target's LIVE hit area contains the point — the same geometry
    /// <see cref="Update(PhysicalPoint, long)"/> tests, so a margin applied by adaptation's
    /// geometry routing is included. State-free: it neither accumulates nor resets dwell, which
    /// makes it safe for an owner's own per-sample gate (e.g. a leave-before-re-arm latch) to ask
    /// "is gaze still on this target?" without perturbing the detector.
    /// </summary>
    /// <returns><c>false</c> for an unknown id (nothing is registered there to be inside).</returns>
    public bool HitTest(string targetId, PhysicalPoint point) =>
        _targets.TryGetValue(targetId, out var target) && target.HitTest(point);
}
