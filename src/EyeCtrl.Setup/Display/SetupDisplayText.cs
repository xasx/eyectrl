namespace EyeCtrl.Setup.Display;

using EyeCtrl.Core.Localization;

/// <summary>One result screen's text and whether it reads as a failure (60 s) or a success (10 s).</summary>
/// <param name="Text">The resolved, formatted text.</param>
/// <param name="IsFailure">True for every outcome that is not a completed success.</param>
public readonly record struct SetupDisplayResult(string Text, bool IsFailure);

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

    /// <summary>The stage line. Update and rollback share the stage texts; an uninstall only has
    /// the stop line to say.</summary>
    public static string Stage(StringTable strings, SetupStage stage) => stage switch
    {
        SetupStage.Verify => strings.T("update.progress.verify"),
        SetupStage.Stop => strings.T("update.progress.stop"),
        SetupStage.Swap => strings.T("update.progress.swap"),
        SetupStage.Start => strings.T("update.progress.start"),
        _ => string.Empty,
    };

    /// <summary>Maps a terminal result to its text and its success/failure reading.</summary>
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

        return new SetupDisplayResult(text, IsFailure(result.Outcome));
    }

    /// <summary>
    /// True for every outcome that is not a completed, working success. <see cref="SetupOutcome.Degraded"/>
    /// is a failure by owner decision Q12 (the previous version runs, but without UIAccess), and
    /// <see cref="SetupOutcome.RolledBack"/> is the update's failure path — the previous version is
    /// healthy, but the update did not work. A cancel is not a failure: nothing changed.
    /// </summary>
    public static bool IsFailure(SetupOutcome outcome) => outcome switch
    {
        SetupOutcome.Ok or SetupOutcome.Uninstalled or SetupOutcome.Repaired or SetupOutcome.Cancelled
            or SetupOutcome.HandedOff => false,
        _ => true,
    };

    /// <summary>The self-close delay for an outcome.</summary>
    public static TimeSpan CloseDelay(SetupOutcome outcome) => CloseDelayFor(IsFailure(outcome));

    /// <summary>The self-close delay for a success/failure reading.</summary>
    public static TimeSpan CloseDelayFor(bool isFailure) =>
        isFailure ? FailureCloseDelay : SuccessCloseDelay;

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
        SetupOutcome.RolledBack => strings.T("update.result.rolledBack", Version(result.Version)),
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
        SetupOutcome.Ok or SetupOutcome.RolledBack => strings.T("rollback.result.ok", Version(result.Version)),
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
