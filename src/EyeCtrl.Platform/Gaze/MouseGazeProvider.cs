namespace EyeCtrl.Platform.Gaze;

using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using System.Runtime.InteropServices;
using EyeCtrl.Platform.Input;
using System.Threading;
using Microsoft.Extensions.Logging;

public partial class MouseGazeProvider : IGazeProvider, IDisposable
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativeMethods.POINT lpPoint);

    private Timer? _timer;
    private readonly int _intervalMs;
    private readonly ILogger? _logger;
    // TickCount64 value when the last fault report was written; 0 means "never logged". Read and
    // written on the timer thread only, so a plain field suffices. One line per 30 s window,
    // matching the per-handler window Fix 1 uses.
    private long _lastFaultLogTicks;

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="MouseGazeProvider"/> class.</summary>
    /// <param name="pollIntervalMs">Cursor sampling interval in milliseconds.</param>
    /// <param name="logger">Optional logger for subscriber faults.</param>
    public MouseGazeProvider(int pollIntervalMs = 30, ILogger? logger = null)
    {
        _intervalMs = pollIntervalMs;
        _logger = logger;
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
        try
        {
            if (GetCursorPos(out var point))
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * MathConstants.MicrosecondsPerMillisecond;
                GazePointReceived?.Invoke(new GazePoint(point.X, point.Y, timestamp, true));
            }
        }
        catch (Exception ex)
        {
            // OnTick is a System.Threading.Timer callback, so it runs on a pooled thread where an
            // unhandled exception terminates the process. The finally below already re-arms the
            // timer; this catch is what stops the fault from being fatal.
            // Rate-limited like Fix 1's handler reporting: a subscriber throwing every tick must
            // cost one log line per window, not one per tick.
            long now = Environment.TickCount64;
            if (_lastFaultLogTicks == 0 || now - _lastFaultLogTicks >= 30_000)
            {
                _lastFaultLogTicks = now;
                try
                {
                    _logger?.LogError(ex, "Gaze subscriber threw during a mouse-provider tick");
                }
                catch
                {
                    // The log call is the one thing this catch invokes; a broken logger must not
                    // re-open the escape the catch closes.
                }
            }
        }
        finally
        {
            // Always re-arm the one-shot timer, even if a subscriber threw, so a single
            // faulty tick doesn't silently stop the provider forever.
            try
            {
                _timer?.Change(_intervalMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
