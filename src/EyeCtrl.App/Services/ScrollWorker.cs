namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Serilog;

public class ScrollWorker : IDisposable
{
    private const int TickIntervalMs = 16;
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

    public void Start(PhysicalPoint anchor)
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

    private void OnTick(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (!_running) return;

            const double lerpFactor = 0.3;
            _currentVx += (_targetVx - _currentVx) * lerpFactor;
            _currentVy += (_targetVy - _currentVy) * lerpFactor;

            if (Math.Abs(_targetVx) < 0.01 && Math.Abs(_currentVx) < 0.01) _currentVx = 0;
            if (Math.Abs(_targetVy) < 0.01 && Math.Abs(_currentVy) < 0.01) _currentVy = 0;

            double deltaX = _currentVx * TickIntervalMs / 1000.0 + _remainderX;
            double deltaY = _currentVy * TickIntervalMs / 1000.0 + _remainderY;

            int intDeltaX = (int)Math.Truncate(deltaX);
            int intDeltaY = (int)Math.Truncate(deltaY);

            _remainderX = deltaX - intDeltaX;
            _remainderY = deltaY - intDeltaY;

            if (intDeltaX != 0 || intDeltaY != 0)
            {
                _inputSimulator.Scroll(_anchor, intDeltaX, intDeltaY);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _timer.Tick -= OnTick;
    }
}
