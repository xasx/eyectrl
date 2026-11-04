namespace EyeCtrl.Setup.Abstractions;

/// <summary>
/// The elevation policy gate (plan §10): Setup reads <c>ConsentPromptBehaviorAdmin</c> and refuses
/// before changing anything when the value is not 0, because a prompt on the secure desktop is
/// unreachable by gaze and nothing may ever wait on one.
/// </summary>
public interface IElevationPolicy
{
    /// <summary>Reads the policy. Missing or unreadable is a refusal (fail closed).</summary>
    ElevationPolicyResult Read();
}

/// <summary>Whether silent elevation is allowed, with the value that decided it.</summary>
public readonly record struct ElevationPolicyResult(bool Allowed, int? ConsentPromptBehaviorAdmin);

/// <summary>The internal jobs the medium instance hands to an elevated one.</summary>
public enum ElevatedJob
{
    PrepareUpdate,
    ApplyUpdate,
    PrepareRunCopy,
    ApplyRollback,
    Revert,
    Uninstall,
    Repair,
}

/// <summary>One elevated request. <see cref="RunId"/> names the private run folder;
/// <see cref="SourceSetupExe"/> is the verified Setup exe a run copy is made from.</summary>
public sealed record ElevatedRequest(
    ElevatedJob Job,
    string? PackageDirectory = null,
    string? RunId = null,
    string? SourceSetupExe = null,
    bool KeepData = true);

/// <summary>The outcome vocabulary the elevated phase reports back to the medium workflow.</summary>
public enum ElevatedPhaseStatus
{
    Ok,
    Blocked,
    VerifyFailed,
    NotApplied,
    Failed,
}

/// <summary>One elevated phase's result.</summary>
public sealed record ElevatedPhaseResult(ElevatedPhaseStatus Status, string? Detail = null)
{
    /// <summary>A successful phase.</summary>
    public static ElevatedPhaseResult Ok { get; } = new(ElevatedPhaseStatus.Ok);

    /// <summary>True when the phase completed and the next phase may run.</summary>
    public bool Succeeded => Status == ElevatedPhaseStatus.Ok;
}

/// <summary>How one elevated child launch ended.</summary>
public enum LauncherOutcome
{
    Exited,
    Blocked,
    TimedOut,
    StillRunning,
    Failed,
}

/// <summary>The launcher's result: an exit code, a refusal, or a bounded-wait timeout.</summary>
public readonly record struct LauncherResult(LauncherOutcome Outcome, int ExitCode = 0, string? Detail = null);

/// <summary>
/// The one place a runas child is started. Production uses ShellExecute <c>runas</c> and waits
/// with a bound; tests record the argv and return a scripted result, so the broker's job mapping
/// and argv are exercised without elevation.
/// </summary>
public interface IElevatedLauncher
{
    /// <summary>Launches an elevated child and waits for it (bounded).</summary>
    LauncherResult Launch(string exePath, IReadOnlyList<string> arguments);
}

/// <summary>
/// The seam between the medium workflow and the elevated phases. Production launches the same exe
/// with ShellExecute <c>runas</c>; tests execute a fake and record the requests.
/// </summary>
public interface IElevationBroker
{
    /// <summary>True when the current process already carries an elevated token.</summary>
    bool IsElevated { get; }

    /// <summary>Runs one request elevated (or in place when already elevated).</summary>
    ElevatedPhaseResult Run(ElevatedRequest request);
}
