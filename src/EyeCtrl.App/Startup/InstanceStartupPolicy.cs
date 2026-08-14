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
    /// <param name="isRestartHandoff"><c>--restart-handoff</c> was passed.</param>
    public static InstanceStartupDecision Evaluate(string? rawId, bool isRestartHandoff)
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

        // NOTE: there is deliberately no --test-mode requirement here.
        //
        // An earlier revision refused a scoped instance that was not in --test-mode, reasoning that
        // forcing MouseGazeProvider kept it off the eye tracker and so turned "concurrency is
        // unsupported" into something enforced. That optimised for the wrong risk. MouseGazeProvider
        // polls the CURSOR and GazeEngine.OnGazePoint then moves the cursor, so a scoped test
        // instance both depends on the user's assistive software driving the cursor and blocks it
        // with its own SendInput — on a machine controlled solely by eye tracking, that is the one
        // configuration guaranteed to interfere with the user's only input channel.
        //
        // The provider is therefore the launcher's choice. Serialising scoped instances is the
        // harness's job (its own plan specifies a run lock), and the property that actually keeps a
        // launch inert is --paused, which stops OnGazePoint before MoveCursor and before the
        // fixation detector. Integration tests here pass it.

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
