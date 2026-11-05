namespace EyeCtrl.Setup;

using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Windows;

/// <summary>The exe's entry point. Parsing is pure; the composition binds the OS seams.</summary>
public static class Program
{
    /// <summary>Runs one command line and returns the process exit code.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // S6f H1: a live process's cwd holds that folder against Directory.Move, and a
        // ShellExecute child inherits its caller's cwd. Enter on the neutral folder before
        // anything else, so a console or shortcut that starts Setup from inside EyeCtrl\ cannot
        // pin the folder the swap has to rename. Every start also carries it explicitly.
        SetupWorkingDirectory.Enter();
        ConsoleSupport.AttachParentConsole();

        var command = SetupCommandLine.Parse(args);
        if (command.Error is { } error)
        {
            ConsoleSupport.WriteLine(error);
            ConsoleSupport.WriteLine(SetupCommandLine.Usage);
            return SetupExitCodes.Usage;
        }

        if (command.Job == SetupJob.Help)
        {
            ConsoleSupport.WriteLine(SetupCommandLine.Usage);
            return SetupExitCodes.Ok;
        }

        // S6c D9: the medium process and every elevated child append the same rolling file. It is
        // created before the composition so a crash while wiring the OS seams still leaves a trace.
        var log = new CompositeSetupLog(
            FileSetupLog.TryCreate(SetupLogContext.For(command, Environment.ProcessId)),
            new ConsoleSetupLog());

        try
        {
            var paths = SetupComposition.ProductionPaths();
            return RunGuarded(
                command, paths, Environment.ProcessPath, log, null,
                () => SetupComposition.CreateProduction(
                    SetupComposition.PreferredStringsDirectory(command), log).Run(command));
        }
        catch (Exception ex)
        {
            return HandleCrash(
                command, ex, log, result => SetupComposition.TryRecordFailure(result, log));
        }
    }

    /// <summary>
    /// C9 S6e fixup-06 M1: the guard's one wiring point, extracted so a test can prove that
    /// <see cref="Main"/> ACTS on the refusal. A held session returns <see cref="SetupExitCodes.Busy"/>
    /// without ever calling <paramref name="runComposition"/>; a free one calls it once and releases
    /// the session (the same thread owns and releases it).
    /// </summary>
    internal static int RunGuarded(
        ParsedSetupCommand command, SetupPaths paths, string? processPath, ISetupLog log,
        string? mutexName, Func<int> runComposition)
    {
        if (!TryEnterSession(command, paths, processPath, log, mutexName, out var session))
            return SetupExitCodes.Busy;

        using (session)
            return runComposition();
    }

    /// <summary>
    /// C9 S6e fixup-05: becomes the one public Setup session before the composition — and so before
    /// any elevation, window, log record or file write. A second start of the same verb exits
    /// cleanly with nothing changed; the App's hand-off guard refuses the same case from its side.
    /// <paramref name="mutexName"/> exists so tests can use a GUID-suffixed, machine-global-free
    /// name (the production name is <see cref="SetupSessionGuard.SessionMutexName"/>).
    /// </summary>
    internal static bool TryEnterSession(
        ParsedSetupCommand command, SetupPaths paths, string? processPath, ISetupLog log,
        string? mutexName, out IDisposable? session)
    {
        session = null;
        if (!SetupSessionGuard.Guards(command.Job))
            return true;

        var wait = SetupSessionGuard.AcquisitionWait(command, paths, processPath);
        session = SetupSessionGuard.TryEnter(wait, mutexName);
        if (session is not null)
            return true;

        log.Warn($"{command.Job}: another Setup session is already running; exiting without changes.");
        return false;
    }

    /// <summary>
    /// An unhandled exception must leave a log line and — on the medium side only — a failed
    /// record where the verb has an installation outcome, and still a clean exit code for the
    /// coordinator's script. Elevated phases log and exit; the parent records (review N1).
    /// </summary>
    internal static int HandleCrash(
        ParsedSetupCommand command, Exception exception, ISetupLog log,
        Action<SetupResult>? record = null)
    {
        // The whole exception, stack included: this is the only trace an elevated child leaves.
        log.Error("Unhandled exception: " + exception);

        // N1: only the medium side writes setup-last.json. An elevated phase only logs; the
        // medium parent maps its Failed exit code and records the one outcome. Writing the record
        // from an elevated process would be a second, redundant write into a user-writable folder.
        if (!command.IsInternal && SetupLogContext.PublicVerb(command.Job) is { } verb)
        {
            record?.Invoke(new SetupResult
            {
                Verb = verb,
                Outcome = SetupOutcome.Failed,
                Time = DateTimeOffset.UtcNow,
                Detail = "unhandled:" + exception.GetType().Name,
            });
        }

        ConsoleSupport.WriteLine($"EyeCtrl.Setup failed: {exception.GetType().Name}");
        return SetupExitCodes.Failed;
    }
}
