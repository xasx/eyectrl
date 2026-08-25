namespace EyeCtrl.Core.Models;

/// <summary>What became of one attempt to bring a window to the foreground.</summary>
public enum ForegroundSwitchOutcome
{
    /// <summary>The window came forward. Nothing to investigate.</summary>
    Switched,

    /// <summary>
    /// The ALT grant never reached the input stream — <c>SendInput</c> accepted fewer events
    /// than it was given — and the switch then failed. The usual cause is UIPI: an elevated
    /// window holds the foreground, so a non-elevated process cannot inject into it.
    /// </summary>
    GrantBlocked,

    /// <summary>
    /// The grant was injected in full and Windows still refused the switch. The foreground
    /// lock, not the grant, is the thing to look at.
    /// </summary>
    SwitchRefused,
}

/// <summary>Outcome of one foreground-switch attempt. Pure data — no Win32.</summary>
/// <param name="InjectedInputs">Events <c>SendInput</c> reported it accepted.</param>
/// <param name="ExpectedInputs">Events handed to <c>SendInput</c> (the ALT down+up pair).</param>
/// <param name="SetForegroundSucceeded">Whether <c>SetForegroundWindow</c> returned true.</param>
public readonly record struct ForegroundSwitchAttempt(
    uint InjectedInputs,
    uint ExpectedInputs,
    bool SetForegroundSucceeded);

/// <summary>
/// Names the cause when a task-switcher selection does not bring its window forward. Pure:
/// the caller (Win32WindowManager) gathers the Win32 facts and passes them in.
///
/// Both prior failures of this path — "switching never happens" (2026-08-18) and the stuck
/// ALT key (2026-08-25) — were reported as bare symptoms and cost a diagnosis round each,
/// because the two calls that can fail here discard everything they know: SendInput's count
/// and SetForegroundWindow's bool. Classifying them turns the next report into a log line.
/// </summary>
public static class ForegroundSwitchDiagnosis
{
    /// <summary>
    /// Classifies an attempt. A successful switch is <see cref="ForegroundSwitchOutcome.Switched"/>
    /// however the grant fared — the grant is only needed when the foreground is locked, so a
    /// blocked tap followed by a successful switch is not a fault.
    /// </summary>
    /// <param name="attempt">The gathered facts about the attempt.</param>
    /// <returns>The outcome worth logging.</returns>
    public static ForegroundSwitchOutcome Classify(ForegroundSwitchAttempt attempt)
    {
        if (attempt.SetForegroundSucceeded) return ForegroundSwitchOutcome.Switched;
        return attempt.InjectedInputs < attempt.ExpectedInputs
            ? ForegroundSwitchOutcome.GrantBlocked
            : ForegroundSwitchOutcome.SwitchRefused;
    }
}
