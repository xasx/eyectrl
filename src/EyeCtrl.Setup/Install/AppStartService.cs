namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;

/// <summary>Why the pass rule accepted or refused a started App. Stable, log-only strings.</summary>
public static class AppStartFailure
{
    public const string NotStarted = "notStarted";
    public const string ExitedBeforeToken = "exitedBeforeToken";
    public const string TokenTimeout = "tokenTimeout";
    public const string NotAliveAfter20s = "notAliveAfter20s";
    public const string MissingTokenLine = "missingTokenLine";
}

/// <summary>One start-and-check attempt.</summary>
public sealed record AppStartOutcome(
    bool Passed, string? FailureReason, int? ProcessId,
    bool StillAlive = false, bool UiAccessOnlyFailure = false)
{
    /// <summary>The App passed all three checks.</summary>
    public static AppStartOutcome Success(int pid) => new(true, null, pid);

    /// <summary>The App did not pass; the caller rolls back.</summary>
    public static AppStartOutcome Fail(string reason, int? pid) => new(false, reason, pid);

    /// <summary>The App is alive but its token (or token line) lacks UIAccess. Q12's last-resort
    /// candidate: the caller keeps it running and reports <c>degraded</c>.</summary>
    public static AppStartOutcome Degraded(int pid, string reason) =>
        new(false, reason, pid, StillAlive: true, UiAccessOnlyFailure: true);
}

/// <summary>
/// Plan §10 step 5's pass rule. The App is started by the MEDIUM Setup with ShellExecute — never
/// from an elevated process, never with CreateProcess — and passes only when all three hold:
/// <list type="number">
/// <item>within 30 s its token reads High IL and UIAccess 1;</item>
/// <item>the process is still alive after 20 s;</item>
/// <item>C10's startup token line says <c>UIAccess=True</c>.</item>
/// </list>
/// A started-but-failed App is killed before the caller swaps back, so no process from the old
/// folder can hold the rename.
/// </summary>
public sealed class AppStartService
{
    private readonly IProcessControl _processes;
    private readonly IAppLogProbe _logProbe;
    private readonly ISetupClock _clock;
    private readonly ISetupLog _log;

    /// <summary>Creates the service.</summary>
    public AppStartService(
        IProcessControl processes, IAppLogProbe logProbe, ISetupClock clock, ISetupLog log)
    {
        _processes = processes;
        _logProbe = logProbe;
        _clock = clock;
        _log = log;
    }

    /// <summary>Starts the App with ShellExecute and runs the three-part pass rule.</summary>
    public AppStartOutcome StartAndCheck(string appExePath)
    {
        var startedAt = _clock.UtcNow;
        int? pid;
        try
        {
            pid = _processes.Start(appExePath);
        }
        catch (Exception ex)
        {
            _log.Warn($"App start threw {ex.GetType().Name}.");
            return AppStartOutcome.Fail(AppStartFailure.NotStarted, null);
        }

        if (pid is not { } processId)
            return AppStartOutcome.Fail(AppStartFailure.NotStarted, null);

        var (facts, failure) = WaitForUiAccessToken(processId, startedAt);
        if (facts is null)
        {
            // Q12: a process that is alive but never gets High + UIAccess is the last-resort
            // candidate. Do NOT kill it; the workflow decides whether it is the last resort.
            if (_processes.IsAlive(processId))
            {
                _log.Warn($"App is alive without UIAccess ({failure}); leaving the decision to the caller.");
                return AppStartOutcome.Degraded(processId, "noUiAccessToken");
            }

            KillQuietly(processId);
            return AppStartOutcome.Fail(failure ?? AppStartFailure.TokenTimeout, processId);
        }

        var remaining = SetupConstants.AliveAfter - (_clock.UtcNow - startedAt);
        if (remaining > TimeSpan.Zero)
            _clock.Sleep(remaining);

        if (!_processes.IsAlive(processId))
        {
            _log.Warn("App died before the 20 s alive check.");
            return AppStartOutcome.Fail(AppStartFailure.NotAliveAfter20s, processId);
        }

        var line = _logProbe.ReadStartupTokenLine(appExePath, startedAt);
        if (line == AppStartupTokenLine.UiAccessTrue)
        {
            _log.Info("App passed the token, alive and startup-line checks.");
            return AppStartOutcome.Success(processId);
        }

        if (line == AppStartupTokenLine.UiAccessFalse)
        {
            _log.Warn("The startup token line says UIAccess=False; leaving the decision to the caller.");
            return AppStartOutcome.Degraded(processId, "tokenLineUiAccessFalse");
        }

        KillQuietly(processId);
        return AppStartOutcome.Fail(AppStartFailure.MissingTokenLine, processId);
    }

    private (EyeCtrl.Core.Input.ProcessTokenFacts? Facts, string? Failure) WaitForUiAccessToken(
        int pid, DateTimeOffset startedAt)
    {
        while (_clock.UtcNow - startedAt < SetupConstants.TokenCheckTimeout)
        {
            if (!_processes.IsAlive(pid))
            {
                _log.Warn("App exited before the token read.");
                return (null, AppStartFailure.ExitedBeforeToken);
            }

            var facts = _processes.ReadToken(pid);
            if (facts.IntegrityLevel == SetupConstants.HighIntegrityLevel && facts.HasUIAccess == true)
                return (facts, null);

            _clock.Sleep(SetupConstants.TokenPollInterval);
        }

        _log.Warn("App token never read High + UIAccess within 30 s.");
        return (null, AppStartFailure.TokenTimeout);
    }

    private void KillQuietly(int pid)
    {
        try
        {
            _processes.Kill(pid);
            _processes.WaitForExit(pid, SetupConstants.ProcessExitWait);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not kill the failed App: {ex.GetType().Name}");
        }
    }
}
