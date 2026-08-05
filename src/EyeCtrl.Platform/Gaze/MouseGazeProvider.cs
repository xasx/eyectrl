namespace EyeCtrl.Platform.Gaze;

using EyeCtrl.Core;
using EyeCtrl.Core.Diagnostics;
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
    // Shared with the gaze fan-out rather than hand-rolled: successive ticks land on different pool
    // threads (the one-shot timer re-arms after each tick), and the reporter's lock is what makes its
    // window bookkeeping visible across that handoff. One line per 30 s window.
    private readonly HandlerFaultReporter _faults;

    private const string SubscriberFault = "Gaze subscriber threw during a mouse-provider tick";

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="MouseGazeProvider"/> class.</summary>
    /// <param name="pollIntervalMs">Cursor sampling interval in milliseconds.</param>
    /// <param name="logger">Optional logger for subscriber faults.</param>
    public MouseGazeProvider(int pollIntervalMs = 30, ILogger? logger = null)
    {
        _intervalMs = pollIntervalMs;
        _faults = new HandlerFaultReporter(
            (message, ex) => logger?.LogError(ex, "{FaultMessage}", message),
            () => Environment.TickCount64 / 1000,
            windowSeconds: 30);
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
            // timer; this catch is what stops the fault from being fatal. The reporter rate-limits
            // and swallows a broken logger, so nothing here can re-open the escape this catch closes.
            _faults.Report(SubscriberFault, ex);
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
