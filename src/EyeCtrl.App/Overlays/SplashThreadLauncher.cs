namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Threading;
using Serilog;

/// <summary>
/// The narrow surface the launcher and handle need (spec §7 factory seam, r4 #9): tests may hand
/// back a fake OR the real window prepared-invisible. Implemented by SplashWindow (public members).
/// </summary>
internal interface ISplashWindow
{
    void OnSplashShown();
    void BeginExit();
    void ShowNoTrackerSentence();
    void ForceClose();
    void FatalClose();
    bool TryReadDpiScale(out double scaleX, out double scaleY);
    event Action? ExitFinished;
}

/// <summary>
/// The DPI report slot (r3 #2): the splash thread reports its scale; only the main thread accepts
/// it, and once discarded (the 500 ms wait timed out) a late report is never read — the main
/// thread's GetDpiForSystem fallback stands. Nothing here writes DpiHelper.
/// </summary>
internal sealed class SplashDpiReport
{
    private readonly TaskCompletionSource<(double Sx, double Sy)> _source =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _discarded;

    internal void Accept(double scaleX, double scaleY) => _source.TrySetResult((scaleX, scaleY));

    internal void Discard() => _discarded = true;

    internal bool TryAccept(int timeoutMs, out double scaleX, out double scaleY)
    {
        scaleX = scaleY = 0;
        if (_discarded) return false;
        if (!_source.Task.Wait(timeoutMs)) return false;
        // No re-check after the wait: Discard() is only ever called after a TryAccept already
        // returned false (SplashThreadHandle.TryGetDpiScale's timeout path), on the same main
        // thread — a discard cannot arrive DURING a wait, so a successful wait is readable.
        (scaleX, scaleY) = _source.Task.Result;
        return true;
    }
}

/// <summary>
/// The marshalled splash surface (spec §7): a thread-safe handle over the splash thread. Every
/// method posts onto the splash dispatcher; calls after the dispatcher shut down (dead-man,
/// fatal path, app exit) are logged no-ops. Only the MAIN thread reads the DPI report.
/// </summary>
internal sealed class SplashThreadHandle
{
    private readonly Dispatcher _splashDispatcher;
    private readonly ISplashWindow _window;
    private readonly SplashDpiReport _dpi = new();
    private volatile bool _disposed;

    internal SplashThreadHandle(Dispatcher splashDispatcher, ISplashWindow window)
    {
        _splashDispatcher = splashDispatcher;
        _window = window;
    }

    internal void ShowNoTrackerSentence() => Post("ShowNoTrackerSentence", w => w.ShowNoTrackerSentence());

    internal void BeginExit() => Post("BeginExit", w => w.BeginExit());

    /// <summary>The fatal-path close: full teardown (controller closed, ExitFinished → dispatcher
    /// shutdown), not just a window close (r4 #11).</summary>
    internal void Close() => Post("Close", w => w.FatalClose());

    /// <summary>Main-thread read: the splash thread's reported scale, ≤ timeoutMs, else fallback.</summary>
    internal bool TryGetDpiScale(int timeoutMs, out double scaleX, out double scaleY)
    {
        scaleX = scaleY = 0;
        if (_disposed) return false;
        var ok = _dpi.TryAccept(timeoutMs, out scaleX, out scaleY);
        if (!ok) _dpi.Discard();                      // the timeout discards late reports forever
        return ok;
    }

    /// <summary>Runs a probe SYNCHRONOUSLY on the splash thread (tests, r4 #7 — a BeginInvoke probe
    /// writes its result after the caller has read it).</summary>
    internal void Inspect(Action<ISplashWindow> probe) =>
        Invoke("Inspect", probe);

    /// <summary>Called by the launcher on the splash thread once the window's DPI is readable.</summary>
    internal void ReportDpi(double scaleX, double scaleY) => _dpi.Accept(scaleX, scaleY);

    /// <summary>App-exit safety: force-shuts the splash dispatcher if it is somehow still alive.
    /// BeginInvokeShutdown alone — the former Send-priority Invoke barrier had no timeout and
    /// blocks forever when the splash thread is stuck (Dispose runs first in App.OnExit, before
    /// the single-instance lock release); a queued barrier serves nothing, the shutdown request
    /// itself is delivered by the dispatcher when it next pumps, or never if it cannot.</summary>
    internal void Dispose()
    {
        _disposed = true;
        try
        {
            _splashDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Splash: dispatcher already gone at dispose");
        }
    }

    private void Post(string what, Action<ISplashWindow> action)
    {
        if (_disposed) return;
        try
        {
            _splashDispatcher.BeginInvoke(() =>
            {
                try { action(_window); }
                catch (Exception ex) { Log.Warning(ex, "Splash: {What} failed on the splash thread", what); }
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Splash: {What} arrived after the dispatcher shut down", what);
        }
    }

    private void Invoke(string what, Action<ISplashWindow> action)
    {
        if (_disposed) return;
        try
        {
            _splashDispatcher.Invoke(() =>
            {
                try { action(_window); }
                catch (Exception ex) { Log.Warning(ex, "Splash: {What} failed on the splash thread", what); }
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Splash: {What} arrived after the dispatcher shut down", what);
        }
    }
}

/// <summary>
/// Starts the splash on its own STA thread (spec §7/§8): the main thread is blocked for the
/// splash's whole visible life, and only a second dispatcher can animate through that. The thread
/// entry is fully contained — any failure degrades to a splash-less, logged start (r2 #1). The
/// main thread's waits are bounded: ≤ 500 ms for the handle, and the DPI wait adds its own ≤ 500 ms
/// (spec §7 rev 3.2). A timed-out start CANCELS the splash before it can show (r4 #8) — the thread
/// checks the cancellation before its one visible step.
/// </summary>
internal static class SplashThreadLauncher
{
    /// <summary>Test seam (r2 #3, r4 #9): tests hand back an InvisibleTestWindow-prepared real
    /// window or a fake — anything implementing ISplashWindow.</summary>
    internal static Func<ISplashWindow>? WindowFactoryForTests { get; set; }

    internal static SplashThreadHandle? Start()
    {
        var ready = new TaskCompletionSource<SplashThreadHandle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The cancel signal is PER START (no shared static): a leftover thread's late cancel
        // read can never be confused by a later Start() — only THIS start's timeout can set it.
        // The source carries no timer and is intentionally not disposed: the leftover thread
        // must still be able to read the token after Start() has returned.
        var cancel = new CancellationTokenSource();
        var thread = new Thread(() => ThreadBody(ready, cancel.Token))
        {
            IsBackground = true,
            Name = "EyeCtrlSplash",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Task.Wait(TimeSpan.FromMilliseconds(500)))
        {
            cancel.Cancel();                           // the thread must not show the window now (r4 #8)
            Log.Warning("Splash: thread did not produce a handle within 500 ms; starting without a splash");
            return null;
        }
        return ready.Task.Result;
    }

    private static void ThreadBody(TaskCompletionSource<SplashThreadHandle?> ready, CancellationToken cancel)
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var window = WindowFactoryForTests is { } factory ? factory() : new SplashWindow();
            var handle = new SplashThreadHandle(dispatcher, window);
            // Spec §6: a fault inside the dispatcher loop degrades to close + shutdown, never a
            // process kill and never a stuck splash.
            dispatcher.UnhandledException += (_, e) =>
            {
                e.Handled = true;                      // the brief's SetHandled() — the API is a property
                Log.Warning(e.Exception, "Splash: unhandled exception on the splash thread; closing");
                // FatalClose (idempotent), not ForceClose: the state must reach Closed, the timers
                // must stop and the "Splash: closed" line must land (spec §7 — acceptance criterion
                // 2 reads the log), exactly like every other close path.
                try { window.FatalClose(); } catch { /* already closing */ }
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            };
            // The window never owns dispatcher shutdown (it cannot know which dispatcher it rides
            // in tests) — the launcher does, on the exit-finished notification.
            window.ExitFinished += () => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);

            // Hand the handle back BEFORE the first visible step, so a timed-out Start() can
            // cancel a splash that never shows (r4 #8) — the token is per-start, so only THIS
            // start's timeout can set it. Residual race (F1): the main thread's wait may time
            // out microseconds AFTER TrySetResult — the thread then reads the token before the
            // Cancel() lands and shows anyway. Bounded and self-healing: the main thread already
            // returned null (splash-less start stands), and OnSplashShown armed the 15 s dead-man,
            // so the stray splash closes itself, ExitFinished shuts the dispatcher down.
            ready.TrySetResult(handle);
            if (cancel.IsCancellationRequested)
            {
                Log.Information("Splash: start was cancelled before the first frame; no splash shown");
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                return;
            }

            // The one visible step needs the WPF window (ISplashWindow does not carry Show):
            // production always has one. A fake that is not a Window throws on this cast — the
            // start degrades to splash-less AND, with the catch's InvokeShutdown, the already
            // delivered dead handle's calls become logged no-ops (they throw onto a shut-down
            // dispatcher and are caught by the handle's own containment).
            ((Window)window).Show();
            window.OnSplashShown();
            // The splash thread reads its own composition target; it never writes DpiHelper (r3 #2).
            // The window was created on this thread, so its dispatcher IS this dispatcher.
            dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (window.TryReadDpiScale(out var sx, out var sy)) handle.ReportDpi(sx, sy);
            });
            Dispatcher.Run();                          // pumps for the whole main-thread blockage
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Splash: thread failed; starting without a splash");
            ready.TrySetResult(null);
            try { Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { }   // dead handle's Invoke/Dispose then throw → caught → logged no-op
        }
    }
}
