namespace EyeCtrl.Platform.Services;

/// <summary>
/// What one executing window action needs to know about its own freshness.
///
/// <para>
/// Two independent things can make an action too old to finish: a later action may have abandoned
/// it (<see cref="IsCancelled"/>), or its own target may have taken so long to answer that the
/// remaining steps would land long after the dwell (<see cref="IsExpired"/>). A body that injects
/// input must stop at either. The expiry half matters when the target recovers on its own, with no
/// later action to cancel the stuck one: the ALT grant must still not be injected seconds late.
/// </para>
/// </summary>
internal readonly struct WindowActionContext
{
    private readonly TimeProvider _time;
    private readonly long _requestedAt;
    private readonly TimeSpan _startDeadline;

    internal WindowActionContext(
        CancellationToken token,
        TimeProvider time,
        long requestedAt,
        TimeSpan startDeadline)
    {
        Token = token;
        _time = time;
        _requestedAt = requestedAt;
        _startDeadline = startDeadline;
    }

    /// <summary>The action's cancellation token; cancelled when a later action abandons it.</summary>
    internal CancellationToken Token { get; }

    /// <summary>Time since the request was made.</summary>
    internal TimeSpan Elapsed => _time.GetElapsedTime(_requestedAt);

    /// <summary>A later action gave up waiting for this one.</summary>
    internal bool IsCancelled => Token.IsCancellationRequested;

    /// <summary>The action has run past the start allowance, so its remaining work is stale.</summary>
    internal bool IsExpired => Elapsed > _startDeadline;
}
