namespace EyeCtrl.Setup.Windows;

using EyeCtrl.Setup.Abstractions;

/// <summary>The wall clock and a real sleep for the pass rule.</summary>
public sealed class SystemSetupClock : ISetupClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public void Sleep(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero)
            Thread.Sleep(duration);
    }
}

/// <summary>Console logging. A WinExe has no console unless it attached to its parent's; writing
/// still succeeds harmlessly when there is none.</summary>
public sealed class ConsoleSetupLog : ISetupLog
{
    /// <inheritdoc />
    public void Info(string message) => Write("INF", message);

    /// <inheritdoc />
    public void Warn(string message) => Write("WRN", message);

    /// <inheritdoc />
    public void Error(string message) => Write("ERR", message);

    private static void Write(string level, string message)
    {
        try
        {
            Console.Error.WriteLine($"{level} {message}");
        }
        catch (Exception)
        {
            // No standard error (WinExe under Explorer): the result record carries the outcome.
        }
    }
}

/// <summary>Creates a GUID-based run id with no braces or separators.</summary>
public sealed class GuidRunIdGenerator : IRunIdGenerator
{
    /// <inheritdoc />
    public string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>The current process's image path, used only as a last-resort verified run-copy source.</summary>
public sealed class WindowsSetupEnvironment : ISetupEnvironment
{
    /// <inheritdoc />
    public string? ProcessPath => Environment.ProcessPath;
}

/// <summary>S6a's safe default for the Q8 window S6e owns: keep the data and uninstall.</summary>
public sealed class KeepUninstallChoice : IUninstallChoice
{
    /// <inheritdoc />
    public UninstallChoiceAnswer Ask() => new(UninstallChoice.Keep);
}

/// <summary>The S6e deletion seam's production implementation: delete <c>%APPDATA%\EyeCtrl</c> and
/// name anything that could not go.</summary>
public sealed class PhysicalAppDataDeletion : IAppDataDeletion
{
    private readonly SetupPaths _paths;

    /// <summary>Creates the deletion over the one app-data root.</summary>
    public PhysicalAppDataDeletion(SetupPaths paths) => _paths = paths;

    /// <inheritdoc />
    public AppDataDeletionResult Delete()
    {
        var failed = new List<string>();
        try
        {
            if (Directory.Exists(_paths.AppDataRoot))
                Directory.Delete(_paths.AppDataRoot, recursive: true);
            return new AppDataDeletionResult(true, []);
        }
        catch (Exception)
        {
            // Name the survivors: the result screen must say what stayed.
            try
            {
                foreach (var file in Directory.EnumerateFiles(
                             _paths.AppDataRoot, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); }
                    catch (Exception) { failed.Add(file); }
                }

                foreach (var directory in Directory.EnumerateDirectories(
                             _paths.AppDataRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(p => p.Length))
                {
                    try { Directory.Delete(directory, recursive: false); }
                    catch (Exception) { /* counted through its files, or empty-delete raced */ }
                }

                if (Directory.Exists(_paths.AppDataRoot))
                    Directory.Delete(_paths.AppDataRoot, recursive: false);
            }
            catch (Exception)
            {
                if (Directory.Exists(_paths.AppDataRoot))
                    failed.Add(_paths.AppDataRoot);
            }
        }

        return new AppDataDeletionResult(failed.Count == 0, failed);
    }
}
