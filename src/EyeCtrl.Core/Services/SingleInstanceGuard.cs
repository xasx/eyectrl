namespace EyeCtrl.Core.Services;

using System.Threading;

/// <summary>
/// Named-mutex single-instance guard: the second process to call <see cref="TryAcquire()"/> fails.
/// <para>
/// <b>Why a keeper thread owns it.</b> Closing a handle does NOT release a named mutex; only
/// <c>ReleaseMutex</c> called on the OWNING thread does. While the mutex was acquired on the
/// caller's (UI) thread, a hung exit could not release it from anywhere else: the dead instance kept
/// <c>Local\EyeCtrl.App.Singleton</c> until the process ended, and a restart replacement queued
/// behind it gave up after its wait. So one dedicated background thread acquires the mutex and then
/// parks on a release signal. The normal exit's last step and the exit watchdog both signal that
/// keeper, which can call <c>ReleaseMutex</c> even while the UI thread is stuck.
/// </para>
/// <para>
/// Startup semantics are unchanged: the same name, the same fail-fast acquire for a duplicate
/// launch, and the same bounded queueing for a restart replacement (<see cref="TryAcquire(TimeSpan)"/>).
/// </para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// Default production mutex name — resolved per build: a dev build takes the
    /// <c>.Dev</c>-suffixed name so it can never block or probe the installed App's instance
    /// (see <see cref="ProfileLocation"/>). Injectable for tests (per-test isolation).
    /// </summary>
    public static string DefaultMutexName => ProfileLocation.Current.AppSingletonMutexName;

    /// <summary>
    /// How long a release waits for the keeper thread before giving up. Bounded because the same
    /// call runs inside <c>App.OnExit</c>'s last step and inside the exit watchdog's pre-FailFast
    /// path: a keeper that cannot finish must hold neither one.
    /// </summary>
    public static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(1);

    /// <summary>The keeper thread's name (test seam: startup must create it named and background).</summary>
    internal const string KeeperThreadName = "EyeCtrl instance-mutex keeper";

    private readonly string _mutexName;
    private readonly ManualResetEventSlim _acquireSettled = new(false);
    private readonly ManualResetEventSlim _releaseRequested = new(false);

    private Thread? _keeper;
    private Mutex? _mutex;
    private volatile bool _owned;
    private int _started;

    /// <summary>Creates a guard for the default production mutex (<see cref="DefaultMutexName"/>).</summary>
    public SingleInstanceGuard() : this(DefaultMutexName) { }

    /// <summary>Creates a guard for a named mutex (injectable for per-test isolation).</summary>
    public SingleInstanceGuard(string mutexName) { _mutexName = mutexName; }

    /// <summary>The keeper thread (test seam: pins that ownership lives off the caller's thread).</summary>
    internal Thread? KeeperThread => _keeper;

    /// <summary>True if this instance acquired the mutex (legitimate to run).</summary>
    public bool TryAcquire() => TryAcquire(TimeSpan.Zero);

    /// <summary>
    /// Acquires the mutex, waiting up to <paramref name="timeout"/> for a current holder
    /// to release it. The acquisition itself runs on the keeper thread; this call only waits for
    /// its verdict, so ownership never lands on the caller's thread.
    ///
    /// <see cref="TimeSpan.Zero"/> — the default and the behaviour every existing caller
    /// gets — fails fast, which is what the double-click guard needs. A restart child
    /// passes a real timeout instead: it must QUEUE behind the instance it is replacing,
    /// because that instance does not release until the end of its own OnExit — after it
    /// has written settings and torn down the gaze device.
    /// </summary>
    /// <param name="timeout">How long to wait for the current holder to release.</param>
    /// <returns><see langword="true"/> if the mutex is now held by this instance.</returns>
    public bool TryAcquire(TimeSpan timeout)
    {
        // One keeper per guard; a second TryAcquire reports the same verdict rather than
        // stacking a second owner (which a named mutex would treat as contention).
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return _owned;

        _keeper = new Thread(() => Keep(timeout))
        {
            IsBackground = true,
            Name = KeeperThreadName,
        };
        _keeper.Start();

        // Unbounded deliberately: the keeper's own wait is bounded by `timeout`, and it always
        // settles this event (the catch and the unconditional assignment in Keep), so this cannot
        // outlive the acquisition attempt.
        _acquireSettled.Wait();
        return _owned;
    }

    /// <summary>
    /// Signals the keeper to release the mutex and waits up to <paramref name="timeout"/> for it to
    /// finish. Safe from any thread — that is the point: the exit watchdog calls it while the UI
    /// thread is hung. Returns <see langword="true"/> when nothing was held or the keeper finished.
    /// </summary>
    /// <param name="timeout">How long to wait for the keeper thread to end.</param>
    public bool Release(TimeSpan timeout)
    {
        Thread? keeper = _keeper;
        if (keeper is null)
            return true;

        _releaseRequested.Set();
        return keeper.Join(timeout);
    }

    /// <summary>
    /// Releases through the keeper (bounded by <see cref="ReleaseWait"/>) and disposes the handle.
    /// Idempotent; never throws.
    /// </summary>
    public void Dispose() => _ = Release(ReleaseWait);

    /// <summary>
    /// The keeper's whole life: acquire on THIS thread, then park until released — so the thread
    /// that owns the mutex is never the UI thread. The handle is disposed here too, on the owner.
    /// </summary>
    private void Keep(TimeSpan timeout)
    {
        bool owned;
        try
        {
            // createdNew==true means no prior owner existed anywhere — this thread got it cleanly.
            _mutex = new Mutex(initiallyOwned: true, name: _mutexName, createdNew: out owned);
            if (!owned)
            {
                // Already owned by some process. WaitOne returns true only if we can take it
                // within the timeout. AbandonedMutex means a prior holder was force-killed —
                // treat as acquired (spec, in-app #2). That case also covers a parent that died
                // mid-exit during a restart handoff: ownership still passes to the child.
                try
                {
                    owned = _mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }
            }
        }
        catch
        {
            owned = false;
        }

        _owned = owned;
        _acquireSettled.Set();

        if (!owned)
        {
            try { _mutex?.Dispose(); } catch { }
            return;
        }

        try { _releaseRequested.Wait(); } catch (ObjectDisposedException) { }

        // Only legal on the owning thread — which is exactly why ownership lives here. The
        // release must still complete when the UI thread is hung, so nothing here waits on it.
        try { _mutex.ReleaseMutex(); } catch { }
        try { _mutex.Dispose(); } catch { }
    }
}
