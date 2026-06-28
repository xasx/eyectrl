namespace EyeCtrl.EyeCare;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>Trigger core: active-session clock + cadence + natural-break suppression.
/// Driven entirely by Update(timestampUs) — no timers, no Task.Delay → fully deterministic.</summary>
public sealed class EyeCareScheduler
{
    private readonly EyeCareSettings _settings;
    private long _activeMicroseconds;       // accumulated active gaze time
    private long _awayRunStartUs;            // start of current looking-away run (0 = not away)
    private bool _naturalBreakTakenThisInterval;

    public EyeCareScheduler(EyeCareSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Raised when a break becomes due (caller then shows the banner via hooks).</summary>
    public event Action? BreakDue;

    /// <summary>Feeds one raw gaze sample + the current paused state.</summary>
    public void Update(GazePoint sample, bool isPaused)
    {
        if (!_settings.Enabled) return;

        // Advance the inter-sample baseline on EVERY call (valid, invalid, paused).
        // Computing the step here — before any early return — means an away/paused gap
        // moves the baseline forward instead of being added to the active total on the
        // first sample after the gap (which would over-count away/paused time).
        var step = ComputeStepMicroseconds(sample.TimestampMicroseconds);

        // ── Track looking-away runs for natural-break suppression ──
        if (!sample.IsValid)
        {
            if (_awayRunStartUs == 0) _awayRunStartUs = sample.TimestampMicroseconds;
            var awayFor = sample.TimestampMicroseconds - _awayRunStartUs;
            if (awayFor >= _settings.NaturalBreakSeconds * 1_000_000L)
                _naturalBreakTakenThisInterval = true;
            return; // away time does NOT accrue (baseline already advanced above)
        }

        // Eyes back; reset away-run tracking
        _awayRunStartUs = 0;

        if (isPaused) return; // paused → no active time accrues (baseline already advanced)

        _activeMicroseconds += step;

        var intervalUs = _settings.IntervalMinutes * 60_000_000L;
        if (_activeMicroseconds >= intervalUs)
        {
            if (!_naturalBreakTakenThisInterval)
                BreakDue?.Invoke();
            // Reset for the next interval regardless
            _activeMicroseconds = 0;
            _naturalBreakTakenThisInterval = false;
        }
    }

    private long? _lastTimestampUs;
    private long ComputeStepMicroseconds(long nowUs)
    {
        var step = (_lastTimestampUs is null) ? 0 : (nowUs - _lastTimestampUs.Value);
        _lastTimestampUs = nowUs;
        return Math.Max(0, step);
    }

    /// <summary>Applies a snooze (caller invokes after the user dismisses the banner).</summary>
    public void Snooze()
    {
        _activeMicroseconds = -_settings.SnoozeMinutes * 60_000_000L;
        _naturalBreakTakenThisInterval = false;
    }

    /// <summary>Resets after a completed break.</summary>
    public void OnBreakCompleted()
    {
        _activeMicroseconds = 0;
        _naturalBreakTakenThisInterval = false;
    }
}
