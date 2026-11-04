namespace EyeCtrl.Setup;

/// <summary>The exe's entry point. Parsing is pure; the composition binds the OS seams.</summary>
public static class Program
{
    /// <summary>Runs one command line and returns the process exit code.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
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
            return SetupComposition.CreateProduction(
                command.Job == SetupJob.Update ? command.PackageDirectory : null).Run(command);
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
