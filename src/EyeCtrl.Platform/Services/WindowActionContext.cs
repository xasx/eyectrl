namespace EyeCtrl.Platform.Services;

/// <summary>
/// What one executing window action needs to know about its own freshness.
///
/// <para>
/// Two independent things can make an action too old to finish its remaining steps: a later action
/// may have abandoned it (<see cref="IsCancelled"/>), or the whole action may have run past its
/// completion budget (<see cref="IsStale"/>) — e.g. the target answered the restore very late. A
/// body that injects input must stop at either. The budget half matters when the target recovers on
/// its own, with no later action to cancel the stuck one: the ALT grant must still not be injected
/// seconds late.
/// </para>
///
/// <para>
/// The budget deliberately starts at the request and is longer than the scheduler's start deadline:
/// a window minimised for a long time can take over a second to restore, and today it still
/// switched. Once the target has answered, the atomic ALT pair and the foreground call may finish
/// within this budget; after it, the pick is dropped instead.
/// </para>
/// </summary>
internal readonly struct WindowActionContext
{
    private readonly TimeProvider _time;
    private readonly long _requestedAt;
    private readonly TimeSpan _completionBudget;

    internal WindowActionContext(
        CancellationToken token,
        TimeProvider time,
        long requestedAt,
        TimeSpan completionBudget)
    {
        Token = token;
        _time = time;
        _requestedAt = requestedAt;
        _completionBudget = completionBudget;
    }

    /// <summary>The action's cancellation token; cancelled when a later action abandons it.</summary>
    internal CancellationToken Token { get; }

    /// <summary>Time since the request was made.</summary>
    internal TimeSpan Elapsed => _time.GetElapsedTime(_requestedAt);

    /// <summary>A later action gave up waiting for this one.</summary>
    internal bool IsCancelled => Token.IsCancellationRequested;

    /// <summary>The action has run past its completion budget, so its remaining work is stale.</summary>
    internal bool IsStale => Elapsed > _completionBudget;
}
