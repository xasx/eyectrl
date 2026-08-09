namespace EyeCtrl.Supervisor.Ipc;

/// <summary>
/// Pure identity-check predicates extracted from <see cref="ClientIdentityBinder.Resolve"/>
/// so the security-relevant boundaries are unit-testable without P/Invoke.
/// </summary>
internal static class IdentityChecks
{
    /// <summary>
    /// PID-reuse guard: a live process's creation time must match the recorded one
    /// within a small tolerance (seconds). Returns true if the tolerance is satisfied.
    /// </summary>
    public static bool CreationTimeWithinTolerance(DateTime actual, DateTime expected, double toleranceSeconds = 2)
    {
        return Math.Abs((actual - expected).TotalSeconds) <= toleranceSeconds;
    }

    /// <summary>
    /// Returns true if the client's session matches the supervisor's own session
    /// (RDP/fast-user-switching guard).
    /// </summary>
    public static bool IsSameSession(uint clientSession, uint ownSession)
    {
        return clientSession == ownSession;
    }

    /// <summary>
    /// Returns true if the client's exe path matches the expected path
    /// (case-insensitive, ordinal).
    /// </summary>
    public static bool ExePathMatches(string actual, string expected)
    {
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
