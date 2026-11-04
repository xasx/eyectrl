namespace EyeCtrl.Setup;

using System.Text.Json;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Display;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Windows;

/// <summary>
/// The production wiring: every seam bound once. The composition is also the place where the
/// internal jobs are dispatched — a job that arrived without an elevated token is refused instead
/// of failing halfway through a Program Files write.
/// </summary>
public sealed class SetupComposition
{
    private readonly SetupWorkflow _workflow;
    private readonly ElevatedOperations _elevated;
    private readonly StatusService _status;
    private readonly IStatusWriter _statusWriter;
    private readonly IElevationBroker _broker;
    private readonly ISetupLog _log;

    private SetupComposition(
        SetupWorkflow workflow, ElevatedOperations elevated,
        StatusService status, IStatusWriter statusWriter, IElevationBroker broker, ISetupLog log)
    {
        _workflow = workflow;
        _elevated = elevated;
        _status = status;
        _statusWriter = statusWriter;
        _broker = broker;
        _log = log;
    }

    /// <summary>Builds the production composition from the machine's OS folders.</summary>
    /// <param name="updatePackageDirectory">
    /// For an update, the inbox package's folder: its <c>strings.de.json</c> comes from the same
    /// build as the code reading it, so the first update from a pre-S6e version still shows German.
    /// </param>
    public static SetupComposition CreateProduction(string? updatePackageDirectory = null)
    {
        var log = new ConsoleSetupLog();
        var paths = new SetupPaths(
            RequireFolder(Environment.SpecialFolder.ProgramFiles),
            RequireFolder(Environment.SpecialFolder.ApplicationData),
            RequireFolder(Environment.SpecialFolder.LocalApplicationData));

        var fileSystem = new PhysicalFileSystem();
        var registry = new WindowsRegistryStore();
        var guard = new PathGuard(paths);
        var policy = new ElevationPolicyGate(registry);
        var signature = new WinTrustFileSignatureVerifier();
        var catalog = new AuthenticodeCatalogVerifier(signature);
        var processes = new WindowsProcessControl();
        var clock = new SystemSetupClock();
        var environment = new WindowsSetupEnvironment();
        var metadata = new PackageMetadataReader(fileSystem);
        var recorder = new ResultRecorder(paths, fileSystem, guard, log);
        var appsAndFeatures = new AppsAndFeaturesWriter(paths, registry, guard, log);
        var runCopies = new RunCopyManager(paths, fileSystem, guard, signature, log);
        var staging = new StagingService(paths, fileSystem, guard, catalog, log);
        var journal = new InstallJournalStore(paths, fileSystem, guard, log);
        var state = new InstallStateMachine(
            paths, fileSystem, registry, guard, appsAndFeatures, metadata, journal, log);
        var stop = new StopCoordinator(paths, processes, new CoreHeldButtonRecovery(), log);
        var appStart = new AppStartService(processes, new WindowsAppLogProbe(paths), clock, log);
        var appDataDeletion = new PhysicalAppDataDeletion(paths);
        var elevated = new ElevatedOperations(
            paths, policy, runCopies, staging, stop, state, appsAndFeatures, appDataDeletion, log);
        var broker = new ShellExecuteElevationBroker(
            paths, elevated, environment, new WindowsElevationState(), policy,
            new ShellExecuteRunAsLauncher(log), log);
        var planner = new SupervisionStartPlanner(new SettingsSnapshotReader(paths, fileSystem));
        var ui = SetupUi.Load(paths, fileSystem, updatePackageDirectory, AppContext.BaseDirectory);
        var workflow = new SetupWorkflow(
            paths, policy, broker, appStart, planner, fileSystem, processes, new WpfUninstallChoice(ui),
            new GuidRunIdGenerator(), metadata, recorder, environment, clock, log,
            new WpfSetupDisplayFactory(ui, paths.AppDataRoot));
        var status = new StatusService(
            paths, state, processes, policy, recorder, registry, journal);

        return new SetupComposition(workflow, elevated, status, new ConsoleStatusWriter(),
            broker, log);
    }

    /// <summary>
    /// The folder whose German table an update should prefer: the inbox package's own folder, whose
    /// table comes from the same build as the code reading it. Every other job — including the
    /// elevated <c>apply-update</c> — returns null, so an elevated job can never read the inbox.
    /// </summary>
    internal static string? PreferredStringsDirectory(ParsedSetupCommand command) =>
        command.Job == SetupJob.Update ? command.PackageDirectory : null;

    /// <summary>Runs one parsed command and returns the process exit code.</summary>
    public int Run(ParsedSetupCommand command)
    {
        if (command.Job == SetupJob.Status)
        {
            var json = JsonSerializer.Serialize(_status.GetReport(), SetupJson.Options);
            _statusWriter.Write(json);
            return SetupExitCodes.Ok;
        }

        if (command.IsInternal)
        {
            if (command.RunId is { } runId && !RunCopyManager.IsValidRunId(runId))
            {
                _log.Error("An internal job has an unsafe run id; refusing before any write.");
                return SetupExitCodes.Usage;
            }

            if (!_broker.IsElevated)
            {
                _log.Error("An internal job needs an elevated token; refusing before any write.");
                return SetupExitCodes.Blocked;
            }

            var phase = _elevated.Execute(ToRequest(command));
            return PhaseExitCode(phase);
        }

        var result = command.Job switch
        {
            SetupJob.Update => _workflow.Update(command.PackageDirectory!),
            SetupJob.Rollback => _workflow.Rollback(),
            SetupJob.Uninstall => _workflow.Uninstall(command.Quiet, command.KeepData),
            SetupJob.Repair => _workflow.Repair(),
            _ => null,
        };

        if (result is null)
        {
            _log.Error($"Unsupported public job '{command.Job}'.");
            return SetupExitCodes.Usage;
        }

        ConsoleSupport.WriteLine(JsonSerializer.Serialize(result, SetupJson.Options));
        return SetupExitCodes.For(result.Outcome);
    }

    internal static ElevatedRequest ToRequest(ParsedSetupCommand command) => command.Job switch
    {
        SetupJob.InternalPrepareUpdate => new ElevatedRequest(
            ElevatedJob.PrepareUpdate, PackageDirectory: command.PackageDirectory, RunId: command.RunId),
        SetupJob.InternalApplyUpdate => new ElevatedRequest(
            ElevatedJob.ApplyUpdate, RunId: command.RunId),
        SetupJob.InternalPrepareRunCopy => new ElevatedRequest(
            ElevatedJob.PrepareRunCopy, RunId: command.RunId, SourceSetupExe: command.SourceSetupExe),
        SetupJob.InternalApplyRollback => new ElevatedRequest(
            ElevatedJob.ApplyRollback, RunId: command.RunId),
        SetupJob.InternalRevert => new ElevatedRequest(
            ElevatedJob.Revert, RunId: command.RunId),
        SetupJob.InternalUninstall => new ElevatedRequest(
            ElevatedJob.Uninstall, RunId: command.RunId, KeepData: command.KeepData ?? true),
        SetupJob.InternalRepair => new ElevatedRequest(
            ElevatedJob.Repair, RunId: command.RunId),
        _ => throw new InvalidOperationException($"Not an internal job: {command.Job}"),
    };

    private static int PhaseExitCode(ElevatedPhaseResult phase) => phase.Status switch
    {
        ElevatedPhaseStatus.Ok => SetupExitCodes.Ok,
        ElevatedPhaseStatus.Blocked => SetupExitCodes.Blocked,
        ElevatedPhaseStatus.VerifyFailed => SetupExitCodes.VerifyFailed,
        ElevatedPhaseStatus.NotApplied => SetupExitCodes.NotApplied,
        _ => SetupExitCodes.Failed,
    };

    private static string RequireFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"The OS folder '{folder}' is unavailable.");
        return path;
    }
}

/// <summary>Writes the status JSON to standard output.</summary>
public sealed class ConsoleStatusWriter : IStatusWriter
{
    /// <inheritdoc />
    public void Write(string json) => ConsoleSupport.WriteLine(json);
}

/// <summary>
/// A WinExe starts without a console. <c>status</c> and the result JSON are meant to be captured
/// by the coordinator's script, so the process attaches to its parent console when there is one.
/// </summary>
public static class ConsoleSupport
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    /// <summary>Attaches to the parent console (no-op when there is none) and flushes standard out.</summary>
    public static void AttachParentConsole()
    {
        try
        {
            _ = AttachConsole(AttachParentProcess);
            var output = Console.OpenStandardOutput();
            if (output.CanWrite)
                Console.SetOut(new StreamWriter(output) { AutoFlush = true });
        }
        catch (Exception)
        {
            // Explorer-launched: no console, and nothing here may fail the install.
        }
    }

    /// <summary>Writes one line, swallowing the no-console failure.</summary>
    public static void WriteLine(string text)
    {
        try
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
        catch (Exception)
        {
            // See AttachParentConsole.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
