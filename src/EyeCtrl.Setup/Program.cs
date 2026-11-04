namespace EyeCtrl.Setup;

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

        try
        {
            return SetupComposition.CreateProduction().Run(command);
        }
        catch (Exception ex)
        {
            // A crash must still be a clean exit code for the coordinator's script. The detail
            // is one type name; no path and no settings content is printed.
            ConsoleSupport.WriteLine($"EyeCtrl.Setup failed: {ex.GetType().Name}");
            return SetupExitCodes.Failed;
        }
    }
}
