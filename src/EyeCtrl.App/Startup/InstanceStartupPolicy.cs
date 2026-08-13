namespace EyeCtrl.App.Startup;

using EyeCtrl.Contracts;

/// <summary>What startup should do about the instance id it was given.</summary>
public enum InstanceStartupOutcome
{
    /// <summary>No id: behave exactly as before this feature existed.</summary>
    RunDefault,

    /// <summary>Valid id and every precondition met: run scoped.</summary>
    RunScoped,

    /// <summary>Refuse to start. Half-scoped is the one state that must be impossible.</summary>
    Refuse
}

/// <summary>The phase-1 decision, and whether it can be logged.</summary>
/// <param name="Outcome">What to do.</param>
/// <param name="InstanceId">
/// Validated id when scoped, and on a refusal that still has a usable id. Always <c>null</c> for
/// <see cref="InstanceStartupOutcome.RunDefault"/> and for the malformed-id refusal — in the latter
/// case deliberately, so no caller can derive a path from a value that
/// <see cref="EyeCtrl.Core.Services.AppPaths.Resolve"/> would throw on.
/// </param>
/// <param name="Reason">Human-readable refusal reason; <c>null</c> unless refusing.</param>
/// <param name="CanLog">
/// Whether a scoped log directory can be derived. <c>false</c> only for a malformed id — see
/// <see cref="InstanceStartupPolicy"/>.
/// </param>
public sealed record InstanceStartupDecision(
    InstanceStartupOutcome Outcome,
    string? InstanceId,
    string? Reason,
    bool CanLog);

/// <summary>
/// Pure phase-1 gate: may this instance run at all? Kept free of I/O so the ordering rules are
/// testable without launching WPF, and so the one branch that must not initialise logging is
/// explicit rather than implied by statement order.
/// </summary>
public static class InstanceStartupPolicy
{
    /// <summary>
    /// Exit code for an isolation refusal. <c>EX_CONFIG</c>, chosen to be distinguishable from the
    /// <c>0</c> a rejected second instance already returns via <c>Shutdown()</c>.
    /// </summary>
    public const int RefusalExitCode = 78;

    /// <summary>
    /// Decides. Evaluation order is load-bearing: the malformed-id branch comes first because it is
    /// the only one that cannot be logged, and a refusal reported as something else would send the
    /// caller down the logging path.
    /// </summary>
    /// <param name="rawId">Raw <see cref="InstanceScope.EnvVar"/> value, or <c>null</c>.</param>
    /// <param name="isTestMode"><c>--test-mode</c> was passed.</param>
    /// <param name="isRestartHandoff"><c>--restart-handoff</c> was passed.</param>
    public static InstanceStartupDecision Evaluate(string? rawId, bool isTestMode, bool isRestartHandoff)
    {
        // Absent is not an error: the default instance keeps today's behaviour byte for byte.
        if (string.IsNullOrWhiteSpace(rawId))
            return new(InstanceStartupOutcome.RunDefault, null, null, CanLog: true);

        if (!InstanceScope.TryParseId(rawId, out var id))
            return new(
                InstanceStartupOutcome.Refuse, null,
                $"{InstanceScope.EnvVar} is set to '{rawId}', which is not a well-formed instance id "
                + $"(letters, digits, '-' and '_' only, at most {InstanceScope.MaxIdLength} characters).",
                CanLog: false);

        // Scoping separates NAMES. It does not make the runtime concurrent: one eye tracker, one
        // tray area, screen-wide overlays. --test-mode forces MouseGazeProvider, which is what turns
        // "concurrency is unsupported" from a comment into something enforced.
        if (!isTestMode)
            return new(
                InstanceStartupOutcome.Refuse, id,
                $"{InstanceScope.EnvVar} requires --test-mode. Instance scoping separates names, not "
                + "the eye tracker, the tray or the overlays, so a scoped instance may not run as a "
                + "real gaze-driven app.",
                CanLog: true);

        // A scoped instance refuses self-restart, so it can never be a handoff replacement.
        if (isRestartHandoff)
            return new(
                InstanceStartupOutcome.Refuse, id,
                "A scoped instance refuses self-restart, so it cannot be a restart handoff "
                + "replacement.",
                CanLog: true);

        return new(InstanceStartupOutcome.RunScoped, id, null, CanLog: true);
    }
}
