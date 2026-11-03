namespace EyeCtrl.App.Services;

/// <summary>
/// The one flag the blocked-input detector and the eye-care prompt share: while Windows is
/// discarding every injected click, the blocked banner wins, so a break due in that state waits for
/// the clear instead of painting its Start/Snooze rings under the banner's buttons. A new block
/// after a clear starts a new wait.
///
/// <para>
/// UI-thread discipline: <see cref="Set"/> is called from the coordinator's dispatcher post, and
/// <see cref="WaitForClearAsync"/> is awaited inside the eye-care dispatcher call. The lock is only
/// around the tiny state swap; the completion is raised outside it.
/// </para>
/// </summary>
public sealed class BlockedInputGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _cleared = Completed();
    private bool _blocked;

    /// <summary>Whether the detector currently reports a block.</summary>
    public bool IsBlocked
    {
        get
        {
            lock (_lock)
                return _blocked;
        }
    }

    /// <summary>
    /// A task that completes when the current block clears. Called while unblocked, it is already
    /// complete, so a caller may use it unconditionally.
    /// </summary>
    public Task WaitForClearAsync()
    {
        lock (_lock)
            return _cleared.Task;
    }

    /// <summary>Sets the flag; the clear completes every waiter registered before this call.</summary>
    /// <param name="blocked">True on a block transition, false on a clear.</param>
    public void Set(bool blocked)
    {
        TaskCompletionSource? toComplete = null;
        lock (_lock)
        {
            if (_blocked == blocked)
                return;

            _blocked = blocked;

            // A new block gets a FRESH incomplete wait; completing the old one is the clear's job.
            // Leaving the previous (already completed) source in place would make a deferral loop
            // spin on a completed task instead of awaiting the clear.
            var previous = _cleared;
            _cleared = blocked ? NewSource() : Completed();
            if (!blocked)
                toComplete = previous;
        }

        // Outside the lock: an await continuation may re-enter this class.
        toComplete?.TrySetResult();
    }

    private static TaskCompletionSource Completed()
    {
        var source = NewSource();
        source.SetResult();
        return source;
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
