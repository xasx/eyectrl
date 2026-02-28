namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class DwellDetector
{
    private readonly Dictionary<string, DwellTarget> _targets = new();
    private long _lastTimestamp;

    public event Action<string>? TargetActivated;
    public event Action<string, double>? TargetProgress;

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

        // Collect activation to fire AFTER iteration completes.
        // Handlers may call ClearTargets/AddTarget which would crash if done during foreach.
        string? activatedId = null;

        foreach (var target in _targets.Values)
        {
            bool inside = target.HitTest(gazePoint);

            if (inside)
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

        // Fire activation after loop — safe for handlers to modify target collection
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
