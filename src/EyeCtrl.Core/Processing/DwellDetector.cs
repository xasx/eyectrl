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

    public void Update(ScreenPoint gazePoint, long timestampMicroseconds)
    {
        _lastTimestamp = timestampMicroseconds;

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
                        TargetActivated?.Invoke(target.Id);
                        target.EnteredTimestamp = null;
                        target.IsGazeInside = false;
                    }
                }
            }
            else
            {
                target.IsGazeInside = false;
                target.EnteredTimestamp = null;
            }
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
