namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Handles gap detection and interpolation for gaze data.
/// All positions are in physical screen pixels.
/// </summary>
public sealed class GapInterpolator
{
    private readonly IGazeFilter _filter;
    private readonly long _maxGapMicroseconds;
    private PhysicalPoint _lastValidPosition = new(0, 0);
    private long _lastValidTimestamp = -1;

    public GapInterpolator(IGazeFilter filter, int maxGapMs)
    {
        _filter = filter;
        _maxGapMicroseconds = maxGapMs * MathConstants.MicrosecondsPerMillisecond;
    }

    /// <summary>
    /// Process a gaze point, returning filtered position in physical screen pixels.
    /// </summary>
    public (PhysicalPoint Position, bool IsInGap) Process(GazePoint rawPoint)
    {
        if (rawPoint.IsValid)
        {
            var filtered = _filter.Filter(rawPoint);
            _lastValidPosition = filtered;
            _lastValidTimestamp = rawPoint.TimestampMicroseconds;
            return (filtered, false);
        }

        // No prior valid sample — can't determine gap duration
        if (_lastValidTimestamp < 0)
            return (_lastValidPosition, false);

        long gap = rawPoint.TimestampMicroseconds - _lastValidTimestamp;

        if (gap >= _maxGapMicroseconds)
        {
            // Gap too long — treat as new sequence, reset filter
            _filter.Reset();
            _lastValidTimestamp = -1;
            return (_lastValidPosition, false);
        }

        // Short gap (blink) — hold position, suppress fixation/dwell
        return (_lastValidPosition, true);
    }

    public void CopyStateFrom(GapInterpolator other)
    {
        _lastValidPosition = other._lastValidPosition;
        _lastValidTimestamp = other._lastValidTimestamp;
    }

    public void Reset()
    {
        _filter.Reset();
        _lastValidPosition = new(0, 0);
        _lastValidTimestamp = -1;
    }
}
