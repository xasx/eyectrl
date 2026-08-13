namespace EyeCtrl.Core.Services;

using System.Threading;

/// <summary>Named-mutex single-instance guard: the second process to call <see cref="TryAcquire"/> fails.</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Default production mutex name. Injectable for tests (per-test isolation).</summary>
    public const string DefaultMutexName = @"Local\EyeCtrl.App.Singleton";
    private readonly string _mutexName;
    private Mutex? _mutex;
    private bool _owned;

    /// <summary>Creates a guard for the default production mutex (<see cref="DefaultMutexName"/>).</summary>
    public SingleInstanceGuard() : this(DefaultMutexName) { }

    /// <summary>Creates a guard for a named mutex (injectable for per-test isolation).</summary>
    public SingleInstanceGuard(string mutexName) { _mutexName = mutexName; }

    /// <summary>
    /// The mutex name this guard binds. Read back by the app's phase-2 instance-scope check, which
    /// verifies what was actually constructed rather than re-deriving what should have been.
    /// </summary>
    public string EffectiveName => _mutexName;

    /// <summary>True if this instance acquired the mutex (legitimate to run).</summary>
    public bool TryAcquire() => TryAcquire(TimeSpan.Zero);

    /// <summary>
    /// Acquires the mutex, waiting up to <paramref name="timeout"/> for a current holder
    /// to release it.
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
        // createdNew==true means no prior owner existed anywhere — we got it cleanly.
        _mutex = new Mutex(initiallyOwned: true, name: _mutexName, createdNew: out _owned);
        if (_owned) return true;

        // Already owned by some process. WaitOne returns true only if we can take it
        // within the timeout. AbandonedMutex means a prior holder was force-killed —
        // treat as acquired (spec, in-app #2). That case also covers a parent that died
        // mid-exit during a restart handoff: ownership still passes to the child.
        try
        {
            _owned = _mutex.WaitOne(timeout); // RETURN the result — do not discard it.
            return _owned;
        }
        catch (AbandonedMutexException)
        {
            _owned = true;
            return true;
        }
    }

    /// <summary>Releases the mutex if held and disposes it.</summary>
    public void Dispose()
    {
        if (_mutex == null) return;
        try { if (_owned) _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
