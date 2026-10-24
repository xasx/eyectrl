namespace EyeCtrl.App.Services.Coordinators;

using EyeCtrl.App.Services;

/// <summary>
/// The lifecycle skeleton all nine coordinators shared by hand: a nullable context set
/// exactly once by a null-checked <see cref="Initialize"/>, an idempotent Dispose that
/// runs per-class cleanup first (<see cref="OnDispose"/>) and unregisters from the
/// messenger last — skipped when never initialized. OnDispose runs even for a
/// coordinator that was never initialized: two of the nine (Keyboard, SettingsUi) own
/// resources (a prediction factory, a settings window) whose teardown must not depend
/// on Initialize having run.
/// </summary>
internal abstract class CoordinatorBase : IDisposable
{
    protected EventCoordinationContext? Context { get; private set; }
    private bool _disposed;

    public void Initialize(EventCoordinationContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        OnInitialize();
    }

    protected abstract void OnInitialize();

    protected virtual void OnDispose() { }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        OnDispose();
        Context?.Messenger.UnregisterAll(this);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the context's dispatcher and WAITS for it: inline when
    /// already there, otherwise a blocking <see cref="System.Windows.Threading.Dispatcher.Invoke(Action)"/>.
    /// The wait is the point. <c>InteractionManager.TogglePause</c> is UI-thread-only, but the
    /// drag's pause hook still marshals defensively — a future off-thread caller must not
    /// fire-and-forget, because the pause must not overtake the held button's release. Not a
    /// production path today.
    /// <para>
    /// Returns <see langword="false"/> when the work could NOT run: a dispatcher that has shut
    /// down or is shutting down, or an
    /// <see cref="System.Windows.Threading.Dispatcher.Invoke(Action)"/> that refuses before
    /// running. (A null dispatcher — focused coordinator tests — runs inline and reports
    /// <see langword="true"/>.) The caller owns the consequence of a skipped teardown — the
    /// drag's claim stays set for dispose to retry, and the pause proceeds anyway.
    /// </para>
    /// </summary>
    protected bool RunOnUiThreadSynchronously(Action work)
    {
        var dispatcher = Context?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            work();
            return true;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return false;

        // The wrapper distinguishes "the delegate ran and threw" (propagate: a real failure) from
        // "Invoke refused before running" (return false: nothing was torn down).
        var ran = false;
        try
        {
            dispatcher.Invoke(() =>
            {
                ran = true;
                work();
            });
        }
        catch (InvalidOperationException) when (!ran)
        {
            return false;
        }
        return ran;
    }
}
