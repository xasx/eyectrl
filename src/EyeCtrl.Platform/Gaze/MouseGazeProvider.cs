namespace EyeCtrl.Platform.Gaze;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using System.Runtime.InteropServices;

public partial class MouseGazeProvider : IGazeProvider, IDisposable
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

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
        _timer = new Timer(OnTick, null, 0, _intervalMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        IsConnected = false;
    }

    private void OnTick(object? state)
    {
        if (GetCursorPos(out var point))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            GazePointReceived?.Invoke(new GazePoint(point.X, point.Y, timestamp, true));
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
