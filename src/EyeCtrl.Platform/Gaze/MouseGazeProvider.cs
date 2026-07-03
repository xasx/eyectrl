namespace EyeCtrl.Platform.Gaze;

using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using System.Runtime.InteropServices;
using EyeCtrl.Platform.Input;
using System.Threading;

public partial class MouseGazeProvider : IGazeProvider, IDisposable
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativeMethods.POINT lpPoint);

    private Timer? _timer;
    private readonly int _intervalMs;

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

    public MouseGazeProvider(int pollIntervalMs = 30)
    {
        _intervalMs = pollIntervalMs;
    }

    public void Start()
    {
        IsConnected = true;
        _timer = new Timer(OnTick, null, 0, Timeout.Infinite);
    }

    public void Stop()
    {
        var timer = _timer;
        _timer = null;
        IsConnected = false;

        if (timer != null)
        {
            using var done = new ManualResetEvent(false);
            if (timer.Dispose(done))
                done.WaitOne(1000);
        }
    }

    private void OnTick(object? state)
    {
        if (GetCursorPos(out var point))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * MathConstants.MicrosecondsPerMillisecond;
            GazePointReceived?.Invoke(new GazePoint(point.X, point.Y, timestamp, true));
        }

        try
        {
            _timer?.Change(_intervalMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
