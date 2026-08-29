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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        OnDispose();
        Context?.Messenger.UnregisterAll(this);
    }

    protected virtual void OnDispose() { }
}
