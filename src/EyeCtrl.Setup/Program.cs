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
            return SetupComposition.CreateProduction(
                SetupComposition.PreferredStringsDirectory(command), log).Run(command);
        }
        catch (Exception ex)
        {
            return HandleCrash(
                command, ex, log, result => SetupComposition.TryRecordFailure(result, log));
        }
    }

    /// <summary>
    /// An unhandled exception must leave a log line and a failed record (where the verb has an
    /// installation outcome), and still a clean exit code for the coordinator's script.
    /// </summary>
    internal static int HandleCrash(
        ParsedSetupCommand command, Exception exception, ISetupLog log,
        Action<SetupResult>? record = null)
    {
        // The whole exception, stack included: this is the only trace an elevated child leaves.
        log.Error("Unhandled exception: " + exception);

        if (SetupLogContext.PublicVerb(command.Job) is { } verb)
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
