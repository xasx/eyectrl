namespace EyeCtrl.Setup;

using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Windows;

/// <summary>
/// What one Setup process logs: the phase (medium or elevated), the verb it is running, and its
/// PID. S6c D9: the elevated children have no console, so this context is what makes a crash
/// traceable from the file alone.
/// </summary>
public sealed record SetupLogContext(string Phase, string Verb, int ProcessId)
{
    /// <summary>The context for a parsed command in this process.</summary>
    public static SetupLogContext For(ParsedSetupCommand command, int processId) =>
        new(command.IsInternal ? "elevated" : "medium", JobName(command.Job), processId);

    /// <summary>The command's verb as it appears in the log.</summary>
    public static string JobName(SetupJob job) => job switch
    {
        SetupJob.Update => "update",
        SetupJob.Rollback => "rollback",
        SetupJob.Uninstall => "uninstall",
        SetupJob.Repair => "repair",
        SetupJob.Status => "status",
        SetupJob.Help => "help",
        SetupJob.InternalPrepareUpdate => "prepare-update",
        SetupJob.InternalApplyUpdate => "apply-update",
        SetupJob.InternalPrepareRunCopy => "prepare-run-copy",
        SetupJob.InternalApplyRollback => "apply-rollback",
        SetupJob.InternalRevert => "revert",
        SetupJob.InternalUninstall => "uninstall",
        SetupJob.InternalRepair => "repair",
        _ => job.ToString().ToLowerInvariant(),
    };

    /// <summary>The public verb an internal job serves, for the crash record. Null for
    /// <c>status</c>/<c>help</c>, which have no installation outcome.</summary>
    public static SetupVerb? PublicVerb(SetupJob job) => job switch
    {
        SetupJob.Update or SetupJob.InternalPrepareUpdate or SetupJob.InternalApplyUpdate
            => SetupVerb.Update,
        SetupJob.Rollback or SetupJob.InternalApplyRollback or SetupJob.InternalRevert
            => SetupVerb.Rollback,
        SetupJob.Uninstall or SetupJob.InternalUninstall => SetupVerb.Uninstall,
        SetupJob.Repair or SetupJob.InternalRepair => SetupVerb.Repair,
        _ => null,
    };
}

/// <summary>
/// The rolling Setup log: one file per day under <c>%APPDATA%\EyeCtrl\logs</c>, appended by the
/// medium process and every elevated child. The path guard allows exactly that pair; every line
/// carries the phase, verb and PID, and no failure here may ever fail the install.
/// </summary>
public sealed class FileSetupLog : ISetupLog
{
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly PathGuard _guard;
    private readonly SetupLogContext _context;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    /// <summary>Creates the log.</summary>
    /// <param name="now">The clock for the file name and the line stamp; tests pin it.</param>
    public FileSetupLog(
        SetupPaths paths, IFileSystem fileSystem, PathGuard guard, SetupLogContext context,
        Func<DateTimeOffset>? now = null)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _guard = guard;
        _context = context;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// Builds the production log over the real app-data folder, or a no-op log when even the path
    /// layout cannot be read (a crash before logging is still a clean exit code).
    /// </summary>
    public static ISetupLog TryCreate(SetupLogContext context, Func<DateTimeOffset>? now = null)
    {
        try
        {
            var paths = SetupComposition.ProductionPaths();
            return new FileSetupLog(paths, new PhysicalFileSystem(), new PathGuard(paths), context, now);
        }
        catch (Exception)
        {
            return NullSetupLog.Instance;
        }
    }

    /// <inheritdoc />
    public void Info(string message) => Write("INF", message);

    /// <inheritdoc />
    public void Warn(string message) => Write("WRN", message);

    /// <inheritdoc />
    public void Error(string message) => Write("ERR", message);

    private void Write(string level, string message)
    {
        try
        {
            var now = _now();
            var path = _paths.SetupLogPath(now);
            lock (_gate)
            {
                _guard.RequireResultRecordDirectory(_paths.AppDataRoot);
                _fileSystem.CreateDirectory(_paths.AppDataRoot);
                _guard.RequireSetupLogDirectory(_paths.LogsRoot);
                _fileSystem.CreateDirectory(_paths.LogsRoot);
                _guard.RequireSetupLogPath(path);

                var line =
                    $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{_context.Phase}] " +
                    $"[{_context.Verb}] [pid {_context.ProcessId}] {message}" +
                    Environment.NewLine;
                _fileSystem.AppendAllText(path, line);
            }
        }
        catch (Exception)
        {
            // Diagnostics must never fail an install. A full disk or a locked folder loses the
            // line, not the operation.
        }
    }
}

/// <summary>Writes one line to every sink, isolating a failing sink from the others.</summary>
public sealed class CompositeSetupLog : ISetupLog
{
    private readonly IReadOnlyList<ISetupLog> _logs;

    /// <summary>Creates the composite.</summary>
    public CompositeSetupLog(params ISetupLog[] logs) => _logs = logs;

    /// <inheritdoc />
    public void Info(string message) => Each(log => log.Info(message));

    /// <inheritdoc />
    public void Warn(string message) => Each(log => log.Warn(message));

    /// <inheritdoc />
    public void Error(string message) => Each(log => log.Error(message));

    private void Each(Action<ISetupLog> write)
    {
        foreach (var log in _logs)
        {
            try
            {
                write(log);
            }
            catch (Exception)
            {
                // One sink must not silence the other.
            }
        }
    }
}

/// <summary>A log that discards everything, for a machine where the log path is unavailable.</summary>
public sealed class NullSetupLog : ISetupLog
{
    /// <summary>The single instance.</summary>
    public static NullSetupLog Instance { get; } = new();

    private NullSetupLog()
    {
    }

    /// <inheritdoc />
    public void Info(string message)
    {
    }

    /// <inheritdoc />
    public void Warn(string message)
    {
    }

    /// <inheritdoc />
    public void Error(string message)
    {
    }
}
