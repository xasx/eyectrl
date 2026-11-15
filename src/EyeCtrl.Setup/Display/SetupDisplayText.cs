namespace EyeCtrl.Setup.Display;

using EyeCtrl.Core.Localization;

/// <summary>The visual state a result screen renders (r4 SD4): Success (bare check, AccentSuccess),
/// Failure incl. Degraded/Blocked (alert, AccentDanger — Q12), Neutral (Cancelled only — dim dash;
/// HandedOff never reaches a display, SetupWorkflow.cs:623-627).</summary>
public enum SetupDisplayState { Success, Failure, Neutral }

/// <summary>One result screen's text and state. State is the single source of truth — the old
/// IsFailure flag is removed (r3 T5): a defaulted flag next to State gave two answers, and a
/// Success default would have painted a check onto a VerifyFailed screen.</summary>
public readonly record struct SetupDisplayResult(string Text, SetupDisplayState State);

/// <summary>
/// The pure mapping from a verb and a <see cref="SetupResult"/> to the strings S6e shows. Kept out
/// of the windows so the mapping is testable without a dispatcher, a theme or a window.
/// </summary>
public static class SetupDisplayText
{
    /// <summary>The detail prefix the elevated uninstall uses when some app-data files survived.</summary>
    public const string DataIncompletePrefix = "dataIncomplete:";

    /// <summary>How long a result stays on screen after a success.</summary>
    public static readonly TimeSpan SuccessCloseDelay = TimeSpan.FromSeconds(10);

    /// <summary>How long a result stays on screen after a failure (Q12's degraded included).</summary>
    public static readonly TimeSpan FailureCloseDelay = TimeSpan.FromSeconds(60);

    /// <summary>The progress screen's title for a verb, or empty for a verb with no screen.</summary>
    public static string ProgressTitle(StringTable strings, SetupVerb verb) => verb switch
    {
        SetupVerb.Update => strings.T("update.progress.title"),
        SetupVerb.Rollback => strings.T("rollback.progress.title"),
        SetupVerb.Uninstall => strings.T("uninstall.working"),
        _ => string.Empty,
    };

    /// <summary>The fixed explanatory line under the title, or empty when the verb has none.</summary>
    public static string ProgressDetail(StringTable strings, SetupVerb verb) => verb switch
    {
        SetupVerb.Update or SetupVerb.Rollback => strings.T("update.progress.detail"),
        _ => string.Empty,
    };

    /// <summary>The stage line. Verify and Stop are shared; Swap and Start are the verb's own,
    /// because a rollback puts the previous version back rather than a new one in place. An
    /// uninstall only has the stop line to say.</summary>
    public static string Stage(StringTable strings, SetupVerb verb, SetupStage stage) => stage switch
    {
        SetupStage.Verify => strings.T("update.progress.verify"),
        SetupStage.Stop => strings.T("update.progress.stop"),
        SetupStage.Swap => verb == SetupVerb.Rollback
            ? strings.T("rollback.progress.swap")
            : strings.T("update.progress.swap"),
        SetupStage.Start => verb == SetupVerb.Rollback
            ? strings.T("rollback.progress.start")
            : strings.T("update.progress.start"),
        _ => string.Empty,
    };

    /// <summary>Maps a terminal result to its text and its display state.</summary>
    /// <param name="strings">The resolved table.</param>
    /// <param name="result">The verb's terminal result.</param>
    /// <param name="appDataRoot">The folder named by <c>uninstall.done.deleteIncomplete</c>.</param>
    public static SetupDisplayResult Result(StringTable strings, SetupResult result, string appDataRoot)
    {
        var text = result.Verb switch
        {
            SetupVerb.Uninstall => UninstallText(strings, result, appDataRoot),
            SetupVerb.Rollback => RollbackText(strings, result),
            _ => UpdateText(strings, result),
        };

        return new SetupDisplayResult(text, StateFor(result.Outcome));
    }

    /// <summary>The state an outcome renders as: Ok/Repaired/Uninstalled succeed, Cancelled is
    /// neutral, everything else — Degraded and Blocked included — is a failure (Q12).</summary>
    public static SetupDisplayState StateFor(SetupOutcome outcome) => outcome switch
    {
        SetupOutcome.Ok or SetupOutcome.Repaired or SetupOutcome.Uninstalled => SetupDisplayState.Success,
        SetupOutcome.Cancelled => SetupDisplayState.Neutral,
        _ => SetupDisplayState.Failure,
    };

    /// <summary>The self-close delay for a state: Failure 60 s (Q12), else 10 s.</summary>
    public static TimeSpan CloseDelayFor(SetupDisplayState state) =>
        state == SetupDisplayState.Failure ? FailureCloseDelay : SuccessCloseDelay;

    /// <summary>The self-close delay for an outcome (kept for the outcome-based callers).</summary>
    public static TimeSpan CloseDelay(SetupOutcome outcome) => CloseDelayFor(StateFor(outcome));

    private static string UninstallText(StringTable strings, SetupResult result, string appDataRoot)
    {
        if (result.Outcome == SetupOutcome.Uninstalled)
        {
            return result.Detail == "deleted"
                ? strings.T("uninstall.done.deleted")
                : strings.T("uninstall.done.kept");
        }

        if (result.Detail is { } detail && detail.StartsWith(DataIncompletePrefix, StringComparison.Ordinal))
            return strings.T("uninstall.done.deleteIncomplete", appDataRoot);

        return result.Outcome switch
        {
            SetupOutcome.Cancelled => strings.T("uninstall.cancelled"),
            SetupOutcome.Blocked => strings.T("uninstall.needsHelp"),
            _ => strings.T("uninstall.failed"),
        };
    }

    private static string UpdateText(StringTable strings, SetupResult result) => result.Outcome switch
    {
        SetupOutcome.Ok or SetupOutcome.Repaired => strings.T("update.result.ok", Version(result.Version)),
        SetupOutcome.RolledBack => strings.T("update.result.rolledBack", Version(result.PreviousVersion)),
        SetupOutcome.Degraded => strings.T("update.result.degraded", Version(result.Version)),
        SetupOutcome.NotApplied => strings.T("update.result.notApplied"),
        SetupOutcome.VerifyFailed => strings.T("update.result.verifyFailed"),
        SetupOutcome.AllFailed => strings.T("update.result.allFailed"),
        SetupOutcome.Blocked => strings.T("uninstall.needsHelp"),
        // G1's still-running child is a "nothing was changed" answer, not a generic failure.
        SetupOutcome.Failed when result.Detail == "childStillRunning" => strings.T("update.result.notApplied"),
        _ => strings.T("update.result.failed"),
    };

    private static string RollbackText(StringTable strings, SetupResult result) => result.Outcome switch
    {
        SetupOutcome.Ok => strings.T("rollback.result.ok", Version(result.Version)),
        SetupOutcome.Degraded => strings.T("rollback.result.degraded", Version(result.Version)),
        SetupOutcome.NotApplied => strings.T("rollback.result.notApplied"),
        SetupOutcome.VerifyFailed => strings.T("rollback.result.verifyFailed"),
        SetupOutcome.AllFailed => strings.T("rollback.result.allFailed"),
        SetupOutcome.Blocked => strings.T("uninstall.needsHelp"),
        _ => strings.T("rollback.result.failed"),
    };

    private static string Version(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "?" : version;
}
