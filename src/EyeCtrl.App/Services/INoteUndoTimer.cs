namespace EyeCtrl.App.Services;

/// <summary>
/// The Undo chip's countdown driver (P2 D5): an injected 100 ms UI-thread timer that runs ONLY
/// while a chip is pending. The manager starts it when the first chip is entered and stops it
/// when the last one resolves; each tick re-reads the injected clock and credits elapsed time to
/// a chip only while Undo is reachable. Production uses a <see cref="DispatcherNoteUndoTimer"/>;
/// headless tests inject a fake they raise by hand. The constructor default is the production
/// implementation, so an unwired argument can never silently stop the clock (the hit tester's
/// fixup-01-M2 lesson).
/// </summary>
public interface INoteUndoTimer : IDisposable
{
    /// <summary>Whether the timer is currently raising ticks.</summary>
    bool IsRunning { get; }

    /// <summary>Starts raising <paramref name="onTick"/> every 100 ms on the UI thread. A start
    /// on an already-running timer restarts it with the new callback.</summary>
    void Start(Action onTick);

    /// <summary>Stops the timer. Stopping a stopped timer is a no-op.</summary>
    void Stop();
}

/// <summary>
/// The production <see cref="INoteUndoTimer"/>: a <c>DispatcherTimer</c> on the constructing
/// thread's dispatcher — the NoteManager's UI thread, so every tick runs in the manager's
/// single-threaded world. Created lazily by the manager's default seam so tests and non-UI
/// constructions never pay for one.
/// </summary>
public sealed class DispatcherNoteUndoTimer : INoteUndoTimer
{
    public const int TickMs = 100;

    private System.Windows.Threading.DispatcherTimer? _timer;

    /// <inheritdoc />
    public bool IsRunning => _timer?.IsEnabled ?? false;

    /// <inheritdoc />
    public void Start(Action onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        Stop();
        _timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            System.Windows.Threading.Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(TickMs)
        };
        _timer.Tick += (_, _) => onTick();
        _timer.Start();
    }

    /// <inheritdoc />
    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
