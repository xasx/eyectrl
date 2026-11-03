namespace EyeCtrl.Setup.Tests.TestSupport;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Install;

/// <summary>The catalog fake: tests configure exactly the refusal they need.</summary>
public sealed class FakeCatalogVerifier : ICatalogVerifier
{
    /// <summary>The result the next Verify returns.</summary>
    public CatalogVerificationResult Result { get; set; } = new(
        SignatureValid: true,
        SignerThumbprint: SetupConstants.PinnedThumbprint,
        ThumbprintMatches: true,
        HashMismatches: [],
        UncataloguedFiles: [],
        Error: null);

    /// <summary>The calls made.</summary>
    public List<string> Calls { get; } = [];

    /// <inheritdoc />
    public CatalogVerificationResult Verify(string catalogPath, string directory, string pinnedThumbprint)
    {
        Calls.Add($"{catalogPath}|{directory}|{pinnedThumbprint}");
        return Result;
    }
}

/// <summary>The file-signature fake.</summary>
public sealed class FakeSignatureVerifier : IFileSignatureVerifier
{
    /// <summary>The result the next Verify returns.</summary>
    public FileSignatureCheck Result { get; set; } =
        new(true, SetupConstants.PinnedThumbprint, null);

    /// <summary>The files checked.</summary>
    public List<string> Calls { get; } = [];

    /// <inheritdoc />
    public FileSignatureCheck Verify(string filePath)
    {
        Calls.Add(filePath);
        return Result;
    }
}

/// <summary>Records every elevated request and optionally executes the real elevated phases.</summary>
public sealed class FakeElevationBroker : IElevationBroker
{
    /// <summary>Whether the fake claims to be elevated.</summary>
    public bool IsElevated { get; set; }

    /// <summary>The request handler; null returns <see cref="Result"/>.</summary>
    public Func<ElevatedRequest, ElevatedPhaseResult>? Handler { get; set; }

    /// <summary>The default result.</summary>
    public ElevatedPhaseResult Result { get; set; } = ElevatedPhaseResult.Ok;

    /// <summary>The requests received.</summary>
    public List<ElevatedRequest> Requests { get; } = [];

    /// <inheritdoc />
    public ElevatedPhaseResult Run(ElevatedRequest request)
    {
        Requests.Add(request);
        return Handler?.Invoke(request) ?? Result;
    }
}

/// <summary>Records every elevated launch and returns a scripted result.</summary>
public sealed class FakeElevatedLauncher : IElevatedLauncher
{
    /// <summary>The launches made.</summary>
    public List<(string ExePath, IReadOnlyList<string> Arguments)> Calls { get; } = [];

    /// <summary>The scripted result.</summary>
    public LauncherResult Result { get; set; } = new(LauncherOutcome.Exited, 0);

    /// <inheritdoc />
    public LauncherResult Launch(string exePath, IReadOnlyList<string> arguments)
    {
        Calls.Add((exePath, arguments.ToList()));
        return Result;
    }
}

/// <summary>A scripted sequence of policy readings, for the per-launch re-check.</summary>
public sealed class SequenceElevationPolicy : IElevationPolicy
{
    private readonly Queue<ElevationPolicyResult> _results = new();

    /// <summary>Adds one answer.</summary>
    public void Enqueue(bool allowed) =>
        _results.Enqueue(new ElevationPolicyResult(allowed, allowed ? 0 : 5));

    /// <inheritdoc />
    public ElevationPolicyResult Read() =>
        _results.Count > 0 ? _results.Dequeue() : new ElevationPolicyResult(true, 0);
}

/// <summary>The whole production object graph over the in-memory seams. Workflow tests use the
/// real elevated operations through the fake broker, so the medium and elevated halves are
/// exercised together without an elevation, a Program Files write or a real process.</summary>
public sealed class SetupHarness
{
    /// <summary>Builds a harness rooted at a fake Program Files.</summary>
    public SetupHarness(string programFilesRoot = @"C:\Program Files")
    {
        Log = [];
        Files = new InMemoryFileSystem(Log);
        Registry = new FakeRegistryStore(Log);
        Processes = new FakeProcessControl(Log);
        Clock = new FakeClock();
        Catalog = new FakeCatalogVerifier();
        Signature = new FakeSignatureVerifier();
        LogProbe = new FakeAppLogProbe();
        HeldButton = new FakeHeldButtonRecovery();
        Choice = new FakeUninstallChoice();
        AppData = new FakeAppDataDeletion();
        Settings = new FakeSettingsReader();
        StatusWriter = new FakeStatusWriter();
        Environment = new FakeSetupEnvironment();
        RunIds = new FakeRunIdGenerator();
        Logging = new RecordingSetupLog();

        Paths = new SetupPaths(
            programFilesRoot, @"C:\Users\test\AppData\Roaming", @"C:\Users\test\AppData\Local");
        Guard = new PathGuard(Paths);
        Policy = new ElevationPolicyGate(Registry);
        Registry.SeedDword(
            Microsoft.Win32.RegistryHive.LocalMachine,
            SetupConstants.PolicyKeyPath,
            SetupConstants.ConsentPromptBehaviorAdmin,
            0);

        Recorder = new ResultRecorder(Paths, Files, Guard, Logging);
        AppsAndFeatures = new AppsAndFeaturesWriter(Paths, Registry, Guard, Logging);
        Metadata = new PackageMetadataReader(Files);
        State = new InstallStateMachine(
            Paths, Files, Registry, Guard, AppsAndFeatures, Metadata, Logging);
        RunCopies = new RunCopyManager(Paths, Files, Guard, Signature, Logging);
        Staging = new StagingService(Paths, Files, Guard, Catalog, Logging);
        Stop = new StopCoordinator(Paths, Processes, HeldButton, Logging);
        AppStart = new AppStartService(Processes, LogProbe, Clock, Logging);
        Elevated = new ElevatedOperations(
            Paths, Policy, RunCopies, Staging, Stop, State, AppsAndFeatures, AppData, Logging);
        Broker = new FakeElevationBroker { Handler = Elevated.Execute };
        Environment.ProcessPath = Paths.InstalledSetupExePath;
        Workflow = new SetupWorkflow(
            Paths, Policy, Broker, AppStart, Files, Processes, Choice, RunIds, Metadata,
            Recorder, Environment, Clock, Logging);
        Status = new StatusService(Paths, State, Processes, Policy, Recorder);
        Planner = new SupervisionStartPlanner(Settings);
        Inbox = new InboxScanner(Paths, Files, Metadata);
    }

    public List<string> Log { get; }
    public SetupPaths Paths { get; }
    public InMemoryFileSystem Files { get; }
    public FakeRegistryStore Registry { get; }
    public FakeProcessControl Processes { get; }
    public FakeClock Clock { get; }
    public FakeCatalogVerifier Catalog { get; }
    public FakeSignatureVerifier Signature { get; }
    public FakeAppLogProbe LogProbe { get; }
    public FakeHeldButtonRecovery HeldButton { get; }
    public FakeUninstallChoice Choice { get; }
    public FakeAppDataDeletion AppData { get; }
    public FakeSettingsReader Settings { get; }
    public FakeStatusWriter StatusWriter { get; }
    public FakeSetupEnvironment Environment { get; }
    public FakeRunIdGenerator RunIds { get; }
    public RecordingSetupLog Logging { get; }
    public PathGuard Guard { get; }
    public ElevationPolicyGate Policy { get; }
    public ResultRecorder Recorder { get; }
    public AppsAndFeaturesWriter AppsAndFeatures { get; }
    public PackageMetadataReader Metadata { get; }
    public InstallStateMachine State { get; }
    public RunCopyManager RunCopies { get; }
    public StagingService Staging { get; }
    public StopCoordinator Stop { get; }
    public AppStartService AppStart { get; }
    public ElevatedOperations Elevated { get; }
    public FakeElevationBroker Broker { get; }
    public SetupWorkflow Workflow { get; }
    public StatusService Status { get; }
    public SupervisionStartPlanner Planner { get; }
    public InboxScanner Inbox { get; }

    /// <summary>Seeds a full installed version (App, Supervisor, Setup, metadata, catalog).</summary>
    public void SeedInstalled(string version = "1.0.0")
    {
        SeedVersionFolder(Paths.InstallRoot, version);
    }

    /// <summary>Seeds the previous version.</summary>
    public void SeedPrevious(string version = "0.9.0")
    {
        SeedVersionFolder(Paths.PreviousRoot, version);
    }

    /// <summary>Seeds staging as an applied-but-not-renamed version.</summary>
    public void SeedStaging(string version = "2.0.0")
    {
        SeedVersionFolder(Paths.StagingRoot, version);
    }

    /// <summary>Seeds a package directory (untrusted inbox shape).</summary>
    public void SeedPackage(string directory, string version)
    {
        Files.AddFile(Path.Combine(directory, SetupConstants.PackageMetadataName), PackageJson(version));
        Files.AddFile(Path.Combine(directory, SetupConstants.SetupExeName));
        Files.AddFile(Path.Combine(directory, SetupConstants.AppExeName));
        Files.AddFile(Path.Combine(directory, SetupConstants.SupervisorExeName));
        Files.AddFile(Path.Combine(directory, SetupConstants.CatalogName));
        Files.AddFile(Path.Combine(directory, "onnxruntime.dll"));
        foreach (var file in Files.EnumerateFiles(directory, recursive: true))
            Files.AddZoneIdentifier(file);
    }

    /// <summary>Seeds the package-shaped app files into one version folder.</summary>
    public void SeedVersionFolder(string directory, string version)
    {
        Files.AddFile(Path.Combine(directory, SetupConstants.PackageMetadataName), PackageJson(version));
        Files.AddFile(Path.Combine(directory, SetupConstants.SetupExeName));
        Files.AddFile(Path.Combine(directory, SetupConstants.AppExeName));
        Files.AddFile(Path.Combine(directory, SetupConstants.SupervisorExeName));
        Files.AddFile(Path.Combine(directory, SetupConstants.CatalogName));
        Files.AddFile(Path.Combine(directory, "onnxruntime.dll"));
    }

    /// <summary>Sets the elevation policy value.</summary>
    public void SetPolicy(int? value)
    {
        if (value is { } present)
        {
            Registry.SeedDword(
                Microsoft.Win32.RegistryHive.LocalMachine,
                SetupConstants.PolicyKeyPath,
                SetupConstants.ConsentPromptBehaviorAdmin,
                present);
        }
        else
        {
            Registry.DeleteValue(
                Microsoft.Win32.RegistryHive.LocalMachine,
                SetupConstants.PolicyKeyPath,
                SetupConstants.ConsentPromptBehaviorAdmin);
        }
    }

    /// <summary>The package.json text.</summary>
    public static string PackageJson(string version) =>
        $$"""{"version":"{{version}}","sha":"abcdef0","created":"2026-11-03T10:00:00Z"}""";

    /// <summary>The relative path of a file under a folder.</summary>
    public static string Relative(string root, string path) => Path.GetRelativePath(root, path);
}
