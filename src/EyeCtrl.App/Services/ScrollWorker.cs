namespace EyeCtrl.App.Services;

using System.Globalization;
using System.Windows.Threading;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Serilog;

public class ScrollWorker : IDisposable
{
    private const int TickIntervalMs = 16;
    private const int ReportIntervalMs = 1000;
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
    private Func<PhysicalPoint?>? _cursorReader;
    private long _wheelsUp;
    private long _wheelsDown;
    private long _wheelsHorizontal;
    private int _msSinceLog;

    private readonly object _lock = new();

    public ScrollWorker(IInputSimulator inputSimulator)
    {
        _inputSimulator = inputSimulator;
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
        Func<PhysicalPoint?>? cursorReader = null)
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
            _cursorReader = cursorReader;
            _wheelsUp = 0;
            _wheelsDown = 0;
            _wheelsHorizontal = 0;
            _msSinceLog = 0;
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
                _inputSimulator.Scroll(_anchor, intDeltaX, intDeltaY);
                if (intDeltaY > 0)
                    _wheelsUp += intDeltaY;
                else if (intDeltaY < 0)
                    _wheelsDown += -intDeltaY;
                if (intDeltaX != 0)
                    _wheelsHorizontal += Math.Abs(intDeltaX);
            }

            // Once per second of ticks — deterministic, not wall-clock, so a paused dispatcher
            // cannot skew the cadence and a test can drive it.
            _msSinceLog += TickIntervalMs;
            if (_msSinceLog >= ReportIntervalMs)
            {
                _msSinceLog -= ReportIntervalMs;
                Log.Information("{Scroll}", Describe());
            }
        }
    }

    private string Describe()
    {
        bool armed = _isArmed?.Invoke() ?? false;
        if (_cursorReader?.Invoke() is { } cursor)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Scroll: wheelsUp={0} wheelsDown={1} h={2} cursor=({3:F0},{4:F0}) cursorToAnchor={5:F0}px armed={6}",
                _wheelsUp, _wheelsDown, _wheelsHorizontal, cursor.X, cursor.Y,
                cursor.DistanceTo(_anchor), armed);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Scroll: wheelsUp={0} wheelsDown={1} h={2} cursor=(?,?) cursorToAnchor=?px armed={3}",
            _wheelsUp, _wheelsDown, _wheelsHorizontal, armed);
    }

    public void Dispose()
    {
        Stop();
        _timer.Tick -= OnTick;
    }
}
