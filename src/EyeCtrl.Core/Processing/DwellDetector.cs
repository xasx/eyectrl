namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;
using System.Collections.Concurrent;

/// <summary>
/// Dwell detector for gaze-based interactions.
/// IMPORTANT: This class is designed for single-threaded use. All public methods must be called
/// from the same thread (typically the UI thread via Dispatcher). The GazeEngineAdapter ensures
/// this by marshaling all gaze events to the UI thread using Dispatcher.BeginInvoke().
/// </summary>
public class DwellDetector
{
    private readonly ConcurrentDictionary<string, DwellTarget> _targets = new();
    private long _cooldownMicroseconds;
    private long _lastTimestamp;
    private long? _lastActivationTimestamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="DwellDetector"/> class.
    /// </summary>
    /// <param name="cooldownMs">Cooldown period in milliseconds after an activation before another can trigger.</param>
    public DwellDetector(int cooldownMs = 0)
    {
        _cooldownMicroseconds = cooldownMs * MathConstants.MicrosecondsPerMillisecond;
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

    /// <summary>Adds a target for dwell detection. Replaces any existing target with the same ID.</summary>
    public void AddTarget(DwellTarget target) => _targets[target.Id] = target;
    /// <summary>Removes a target from dwell detection.</summary>
    public void RemoveTarget(string id) => _targets.TryRemove(id, out _);
    /// <summary>Removes all targets from dwell detection.</summary>
    public void ClearTargets() => _targets.Clear();

    /// <summary>
    /// Resets accumulated dwell state on all targets without removing them.
    /// Call this when switching input contexts (e.g. a modal dialog opening or closing)
    /// to prevent a stale <c>EnteredTimestamp</c> from causing a spurious immediate
    /// activation when <see cref="Update"/> resumes after a pause.
    /// </summary>
    public void ResetAllState()
    {
        foreach (var t in _targets.Values)
        {
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
    /// Updates the dwell detector with the current gaze position and timestamp.
    /// Must be called from the UI thread (enforced by GazeEngineAdapter's Dispatcher marshaling).
    /// </summary>
    /// <param name="gazePoint">Current gaze position in physical screen pixels</param>
    /// <param name="timestampMicroseconds">Timestamp in microseconds</param>
    public void Update(PhysicalPoint gazePoint, long timestampMicroseconds)
    {
        _lastTimestamp = timestampMicroseconds;

        // Cooldown: skip all dwell accumulation if within cooldown period
        if (_lastActivationTimestamp.HasValue &&
            (timestampMicroseconds - _lastActivationTimestamp.Value) < _cooldownMicroseconds)
        {
            // Reset all targets during cooldown (prevent partial accumulation)
            foreach (var t in _targets.Values)
            {
                if (t.IsGazeInside)
                    TargetProgress?.Invoke(t.Id, 0);
                t.IsGazeInside = false;
                t.EnteredTimestamp = null;
            }
            return;
        }
        _lastActivationTimestamp = null; // cooldown elapsed, clear it

        // ── Find the single nearest target within hit range ──
        string? nearestId = null;
        double nearestDistSq = double.MaxValue;

        foreach (var target in _targets.Values)
        {
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

        // ── Accumulate dwell on nearest only; reset all others ──
        string? activatedId = null;

        foreach (var target in _targets.Values)
        {
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
                        target.EnteredTimestamp = null;
                        target.IsGazeInside = false;
                        _lastActivationTimestamp = timestampMicroseconds;
                    }
                }
            }
            else
            {
                if (target.IsGazeInside)
                    TargetProgress?.Invoke(target.Id, 0);
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
}
