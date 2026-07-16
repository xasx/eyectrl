namespace EyeCtrl.App.Services;

using System.Threading;

public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Default production mutex name. Injectable for tests (per-test isolation).</summary>
    public const string DefaultMutexName = @"Local\EyeCtrl.App.Singleton";
    private readonly string _mutexName;
    private Mutex? _mutex;
    private bool _owned;

    public SingleInstanceGuard() : this(DefaultMutexName) { }
    public SingleInstanceGuard(string mutexName) { _mutexName = mutexName; }

    /// <summary>True if this instance acquired the mutex (legitimate to run).</summary>
    public bool TryAcquire()
    {
        // createdNew==true means no prior owner existed anywhere — we got it cleanly.
        _mutex = new Mutex(initiallyOwned: true, name: _mutexName, createdNew: out _owned);
        if (_owned) return true;

        // Already owned by some process. A zero-timeout wait returns true only if we
        // can take it now (e.g. it was released); false if still held. AbandonedMutex
        // means a prior holder was force-killed — treat as acquired (spec, in-app #2).
        try
        {
            _owned = _mutex.WaitOne(0); // RETURN the result — do not discard it.
            return _owned;
        }
        catch (AbandonedMutexException)
        {
            _owned = true;
            return true;
        }
    }

    public void Dispose()
    {
        if (_mutex == null) return;
        try { if (_owned) _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
