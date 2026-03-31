namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

public class NearestKeyTracker
{
    private readonly long _dwellTimeMicroseconds;
    private Dictionary<string, ScreenPoint> _keys = new();
    private string? _currentNearestKeyId;
    private long _enteredTimestamp;

    public NearestKeyTracker(int dwellTimeMs)
    {
        _dwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
    }

    public void SetKeys(Dictionary<string, ScreenPoint> keys)
    {
        _keys = keys;
        _currentNearestKeyId = null;
    }

    public void Clear()
    {
        _keys.Clear();
        _currentNearestKeyId = null;
    }

    public NearestKeyResult Update(ScreenPoint gazePoint, long timestampMicroseconds)
    {
        if (_keys.Count == 0)
            return new NearestKeyResult(null, 0, false);

        // Find nearest key by Euclidean distance
        string? nearestId = null;
        double nearestDistSq = double.MaxValue;

        foreach (var (id, center) in _keys)
        {
            var dx = gazePoint.X - center.X;
            var dy = gazePoint.Y - center.Y;
            var distSq = dx * dx + dy * dy;
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestId = id;
            }
        }

        if (nearestId != _currentNearestKeyId)
        {
            // Nearest key changed -- reset dwell
            _currentNearestKeyId = nearestId;
            _enteredTimestamp = timestampMicroseconds;
            return new NearestKeyResult(nearestId, 0, false);
        }

        // Same key -- accumulate dwell
        var elapsed = timestampMicroseconds - _enteredTimestamp;
        var progress = Math.Min(1.0, (double)elapsed / _dwellTimeMicroseconds);
        var activated = elapsed >= _dwellTimeMicroseconds;

        if (activated)
        {
            _currentNearestKeyId = null; // reset after activation
        }

        return new NearestKeyResult(nearestId, progress, activated);
    }
}

public record NearestKeyResult(string? NearestKeyId, double Progress, bool Activated);
