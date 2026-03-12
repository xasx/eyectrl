namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class DwellDetector
{
    private readonly Dictionary<string, DwellTarget> _targets = new();
    private long _cooldownMicroseconds;
    private long _lastTimestamp;
    private long? _lastActivationTimestamp;

    public DwellDetector(int cooldownMs = 0)
    {
        _cooldownMicroseconds = cooldownMs * 1000L;
    }

    public event Action<string>? TargetActivated;
    public event Action<string, double>? TargetProgress;

    public void SetCooldown(int cooldownMs) => _cooldownMicroseconds = cooldownMs * 1000L;

    public void AddTarget(DwellTarget target) => _targets[target.Id] = target;
    public void RemoveTarget(string id) => _targets.Remove(id);
    public void ClearTargets() => _targets.Clear();

    /// <summary>
    /// Update a target's center position without resetting dwell state.
    /// Used by toolbar/overlays that need to track moving screen positions.
    /// </summary>
    public void UpdateTargetCenter(string id, ScreenPoint newCenter)
    {
        if (_targets.TryGetValue(id, out var target))
            target.Center = newCenter;
    }

    public void Update(ScreenPoint gazePoint, long timestampMicroseconds)
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

    public double GetProgress(string targetId)
    {
        if (!_targets.TryGetValue(targetId, out var target)) return 0;
        if (!target.IsGazeInside || !target.EnteredTimestamp.HasValue) return 0;
        long elapsed = _lastTimestamp - target.EnteredTimestamp.Value;
        return Math.Min(1.0, (double)elapsed / target.DwellTimeMicroseconds);
    }
}
