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
    /// The wait is the point. The paused-teardown hooks (<c>InteractionManager.TogglePause</c>'s
    /// cancel-drag/exit-repeat/exit-scroll) must finish the mode's teardown — above all a
    /// synthesized button release — BEFORE the state becomes Paused, and a queued
    /// <c>BeginInvoke</c> would let the pause overtake it (A5's ordering contract). A null
    /// dispatcher (focused coordinator tests) runs inline.
    /// </summary>
    protected void RunOnUiThreadSynchronously(Action work)
    {
        var dispatcher = Context?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            work();
        else
            dispatcher.Invoke(work);
    }
}
