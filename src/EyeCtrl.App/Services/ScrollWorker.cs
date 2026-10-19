namespace EyeCtrl.App.Services;

using System.Globalization;
using System.Windows.Threading;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Serilog;

/// <summary>
/// Drives scroll mode's wheels on a 16-ms dispatcher timer. Part B (scroll-at-P) deliberately
/// relaxes the scroll-start rule "no per-tick MOVE to the anchor": on a tick that sends a wheel,
/// when the OS cursor is more than <see cref="RestoreThresholdPx"/> away from the anchor (an
/// external gaze mouse moved it), the tick sends MOVE→anchor, the wheel, MOVE→back in ONE atomic
/// batch through <see cref="IInputSimulator.ScrollAtAndRestore"/>. The net cursor position is
/// unchanged; the hop exists only so the wheel is hit-tested at the anchor. Within the threshold
/// the bare <see cref="IInputSimulator.Scroll"/> is sent, exactly as before.
/// </summary>
public class ScrollWorker : IDisposable
{
    private const int TickIntervalMs = 16;
    private const int ReportIntervalMs = 1000;

    /// <summary>Part B fast path: a cursor this close to the anchor keeps the bare wheel (no hop).
    /// The proposal and the owner's decision use 8 physical pixels.</summary>
    internal const double RestoreThresholdPx = 8;
    private readonly IInputSimulator _inputSimulator;
    private readonly DispatcherTimer _timer;

    private PhysicalPoint _anchor;
    private double _targetVx;
    private double _targetVy;
    private double _currentVx;
    private double _currentVy;
    private double _remainderX;
    private double _remainderY;
    private bool _running;

    // F-S5 (scroll start fix): the per-second diagnosis — notches sent, where the OS cursor is,
    // and whether the joystick has armed. The callbacks stay null-tolerant for bare harnesses.
    private Func<bool>? _isArmed;
    // Scroll arming fix (C5): the closest the gaze came to the anchor while unarmed; -1 until the
    // first gated sample.
    private Func<double>? _nearestPx;
    private Func<PhysicalPoint?>? _cursorReader;
    private long _wheelsUp;
    private long _wheelsDown;
    private long _wheelsHorizontal;
    private long _restored;
    private int _msSinceLog;

    // F5 (fixup 01): wall-clock ms since the previous line — the tick-gate is nominal, and the
    // real DispatcherTimer cadence under Windows' timer resolution can differ.
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private long _lastReportMs;
    private readonly Func<long> _nowMs;

    private readonly object _lock = new();

    public ScrollWorker(IInputSimulator inputSimulator, Func<long>? nowMs = null)
    {
        _inputSimulator = inputSimulator;
        // Fixup 02 (c): the log's elapsed value is injectable so a test can pin it; the default is
        // the Stopwatch restarted in Start.
        _nowMs = nowMs ?? (() => _clock.ElapsedMilliseconds);
        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(TickIntervalMs)
        };
        _timer.Tick += OnTick;
    }

    /// <summary>Notches sent this session, all axes, for the exit line.</summary>
    internal long TotalWheels
    {
        get { lock (_lock) return _wheelsUp + _wheelsDown + _wheelsHorizontal; }
    }

    public void Start(
        PhysicalPoint anchor,
        Func<bool>? isArmed = null,
        Func<PhysicalPoint?>? cursorReader = null,
        Func<double>? nearestPx = null)
    {
        lock (_lock)
        {
            _anchor = anchor;
            _targetVx = 0;
            _targetVy = 0;
            _currentVx = 0;
            _currentVy = 0;
            _remainderX = 0;
            _remainderY = 0;
            _isArmed = isArmed;
            _nearestPx = nearestPx;
            _cursorReader = cursorReader;
            _wheelsUp = 0;
            _wheelsDown = 0;
            _wheelsHorizontal = 0;
            _restored = 0;
            _msSinceLog = 0;
            _lastReportMs = 0;
            _clock.Restart();
            _running = true;
        }
        _timer.Start();
        Log.Debug("ScrollWorker started at anchor ({X:F0}, {Y:F0})", anchor.X, anchor.Y);
    }

    public void Stop()
    {
        _timer.Stop();
        lock (_lock)
        {
            _running = false;
        }
        Log.Debug("ScrollWorker stopped");
    }

    public void UpdateTargetVelocity(double vx, double vy)
    {
        lock (_lock)
        {
            _targetVx = vx;
            _targetVy = vy;
        }
    }

    private void OnTick(object? sender, EventArgs e) => Tick();

    /// <summary>One timer tick. Internal so G8 can drive the worker deterministically.</summary>
    internal void Tick()
    {
        lock (_lock)
        {
            if (!_running) return;

            ScrollPhysics.Tick(
                ref _currentVx,
                ref _currentVy,
                _targetVx,
                _targetVy,
                ref _remainderX,
                ref _remainderY,
                TickIntervalMs,
                out int intDeltaX,
                out int intDeltaY);

            if (intDeltaX != 0 || intDeltaY != 0)
            {
                // B2: read the cursor ONCE on a tick that sends a wheel (the per-second line reads
                // it separately). Away from the anchor — an external gaze mouse moved it — hop to
                // the anchor and straight back in one atomic batch. A failed read (null) keeps the
                // bare wheel: there is no safe position to restore to.
                var cursor = _cursorReader?.Invoke();
                if (cursor is { } current && current.DistanceTo(_anchor) > RestoreThresholdPx)
                {
                    _inputSimulator.ScrollAtAndRestore(_anchor, current, intDeltaX, intDeltaY);
                    _restored++;
                }
                else
                {
                    _inputSimulator.Scroll(_anchor, intDeltaX, intDeltaY);
                }
                if (intDeltaY > 0)
                    _wheelsUp += intDeltaY;
                else if (intDeltaY < 0)
                    _wheelsDown += -intDeltaY;
                if (intDeltaX != 0)
                    _wheelsHorizontal += Math.Abs(intDeltaX);
            }

            // Once per second of ticks — deterministic, not wall-clock, so a paused dispatcher
            // cannot skew the cadence and a test can drive it. The line carries the REAL wall-clock
            // interval since the previous line (F5).
            _msSinceLog += TickIntervalMs;
            if (_msSinceLog >= ReportIntervalMs)
            {
                _msSinceLog -= ReportIntervalMs;
                long nowMs = _nowMs();
                long elapsedMs = nowMs - _lastReportMs;
                _lastReportMs = nowMs;
                Log.Information("{Scroll}", Describe(elapsedMs));
            }
        }
    }

    private string Describe(long elapsedMs)
    {
        bool armed = _isArmed?.Invoke() ?? false;
        double nearest = _nearestPx?.Invoke() ?? -1;
        if (_cursorReader?.Invoke() is { } cursor)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Scroll: wheelsUp={0} wheelsDown={1} h={2} cursor=({3:F0},{4:F0}) cursorToAnchor={5:F0}px armed={6} restored={7} nearest={8:F0} elapsedMs={9}",
                _wheelsUp, _wheelsDown, _wheelsHorizontal, cursor.X, cursor.Y,
                cursor.DistanceTo(_anchor), armed, _restored, nearest, elapsedMs);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Scroll: wheelsUp={0} wheelsDown={1} h={2} cursor=(?,?) cursorToAnchor=?px armed={3} restored={4} nearest={5:F0} elapsedMs={6}",
            _wheelsUp, _wheelsDown, _wheelsHorizontal, armed, _restored, nearest, elapsedMs);
    }

    public void Dispose()
    {
        Stop();
        _timer.Tick -= OnTick;
    }
}
