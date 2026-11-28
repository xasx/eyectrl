namespace EyeCtrl.Core.Keyboard;

/// <summary>What the direct <c>IsPassword</c> poll last decided about the focused field.</summary>
public enum PollOutcome
{
    /// <summary>No poll has decided anything yet (or none is wired): the event path alone speaks.</summary>
    NotPolled = 0,

    /// <summary>The poll read the focused element and it is not a password field.</summary>
    NotSensitive,

    /// <summary>The poll read a password field.</summary>
    Sensitive,

    /// <summary>
    /// The poll could not answer (time box, a still-running abandoned read, a provider fault). Learning, the
    /// suggestion bar and the language-model prompt fail CLOSED on it; typing itself is never touched.
    /// </summary>
    Unknown,
}

/// <summary>
/// D3: the keyboard's password verdict from the direct poll. The poll DECIDES: a <see cref="PollOutcome.Sensitive"/>
/// verdict stays until a NEWER poll replaces it — the UIA focus-event path can only raise the combined state
/// (the keyboard ORs the two), it has no way to clear this one.
///
/// <para>
/// <b>Generation-ordered.</b> Every poll carries the generation <see cref="KeyboardSensitiveCheck"/> gave it when
/// the check began (the #396 slot-owner generation). A result is applied only when its generation is newer than
/// the one already applied, so a slow, stale read that finally answers can never clear a verdict a later check
/// has already decided.
/// </para>
///
/// <para>
/// <b>Lock-free to read.</b> The state is one immutable reference; the gaze-rate reads (the bar's refresh, every
/// learn gate) never take a lock. Only <see cref="Apply"/> does, and it runs off the UI thread.
/// </para>
/// </summary>
public sealed class KeyboardPollVerdict
{
    private sealed record State(int Generation, PollOutcome Outcome);

    private readonly object _gate = new();
    private volatile State _state = new(0, PollOutcome.NotPolled);

    /// <summary>The latest applied outcome.</summary>
    public PollOutcome Current => _state.Outcome;

    /// <summary>The generation of the latest applied outcome (0 before any).</summary>
    public int Generation => _state.Generation;

    /// <summary>The poll saw a password field.</summary>
    public bool IsSensitive => _state.Outcome == PollOutcome.Sensitive;

    /// <summary>The poll could not answer: the fail-closed gates treat it like a password field.</summary>
    public bool IsUnknown => _state.Outcome == PollOutcome.Unknown;

    /// <summary>
    /// Applies a poll's outcome unless a newer generation already did. Returns whether it was applied and
    /// what the outcome was before, so the caller can log a CHANGE rather than every poll.
    /// </summary>
    public (bool Applied, PollOutcome Previous) Apply(int generation, PollOutcome outcome)
    {
        lock (_gate)
        {
            State current = _state;
            if (generation <= current.Generation)
                return (false, current.Outcome);
            _state = new State(generation, outcome);
            return (true, current.Outcome);
        }
    }
}
