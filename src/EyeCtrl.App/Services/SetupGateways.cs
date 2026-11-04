namespace EyeCtrl.App.Services;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Windows;

/// <summary>
/// One update package the „Erweitert" page may offer: the inbox folder that passed the scan and
/// the catalog check, its Setup exe, and the two versions its description names.
/// </summary>
public sealed record SetupUpdateOffer(
    string Directory,
    string SetupExePath,
    string Version,
    string InstalledVersion);

/// <summary>
/// C9 S6f's view of the installed Setup: what the „Erweitert" page may offer, and the three
/// hand-offs it can start. Split from the WPF page sources so every decision (visibility matrix,
/// policy gate, ShellExecute shape, clean exit) is headlessly testable with fakes.
/// </summary>
/// <remarks>
/// <para><b>Freshness (review 01 M1):</b> the App builds a NEW instance for every Settings window
/// (a factory through <see cref="SettingsUiCoordinator" />), so the offer and the policy are
/// re-probed on each visit; nothing is cached across windows, let alone the App's lifetime.</para>
/// <para><b>The catalog check is never run on the UI thread (review 01 N2):</b> it hashes and
/// WinTrust-checks every package file and measured 0.5–0.8 s warm and 1–3 s cold. The page calls
/// <see cref="BeginUpdateOfferProbe" /> and renders a same-height placeholder until the result
/// lands; <see cref="UpdateOffer" /> only ever returns a completed result.</para>
/// </remarks>
public interface ISetupGateways
{
    /// <summary>True when this process runs from <c>C:\Program Files\EyeCtrl</c> — the ticket's
    /// definition of "installed". A dev build never is, so it offers none of the three rows.</summary>
    bool Installed { get; }

    /// <summary>True when <c>C:\Program Files\EyeCtrl.previous</c> exists.</summary>
    bool PreviousAvailable { get; }

    /// <summary>The previous package's version, when readable. Names it in the rollback description.</summary>
    string? PreviousVersion { get; }

    /// <summary>True when <c>ConsentPromptBehaviorAdmin</c> is not 0 (plan §10's gate): an update or
    /// rollback would raise a secure-desktop prompt that gaze cannot reach.</summary>
    bool PolicyBlocks { get; }

    /// <summary>The completed catalog verification's result (null when it found no offer). Null also
    /// while the probe is still running — read <see cref="UpdateOfferPending" /> to tell them apart.</summary>
    SetupUpdateOffer? UpdateOffer { get; }

    /// <summary>True while the first catalog verification has not completed yet.</summary>
    bool UpdateOfferPending { get; }

    /// <summary>
    /// Starts the catalog verification once, on a worker thread; later calls are no-ops. The
    /// callback runs once on that worker thread when the result is stored (marshal it yourself).
    /// </summary>
    void BeginUpdateOfferProbe(Action whenDone);

    /// <summary>
    /// Hand off to the offered package's Setup: re-check the Setup exe's signature against the
    /// pinned thumbprint (belt and braces after the catalog check), start
    /// <c>EyeCtrl.Setup.exe update "&lt;dir&gt;"</c> with ShellExecute from a neutral working
    /// directory, then exit cleanly — the lifecycle is <see cref="ISetupExitFlow" />, which must
    /// never set the restart flag. False when refused or the launch threw; EyeCtrl then keeps running.
    /// </summary>
    bool StartUpdateHandoff(SetupUpdateOffer offer);

    /// <summary>Start the installed Setup's <c>rollback</c> with ShellExecute, then exit cleanly.
    /// False when refused or the launch threw; EyeCtrl then keeps running.</summary>
    bool StartRollbackHandoff();

    /// <summary>Start the installed Setup's <c>uninstall</c> with ShellExecute. EyeCtrl keeps
    /// running: its dwell is the input for Setup's own uninstall window. False when refused or the
    /// launch threw.</summary>
    bool StartUninstallHandoff();
}

/// <summary>Starts a process. <paramref name="workingDirectory" /> is the reviewed
/// shell-vs-CreateProcess decision's third leg (review 01 H1): it must be a NEUTRAL folder, never
/// anywhere under <c>Program Files\EyeCtrl*</c>, because a live cwd there blocks the swap's
/// rename. <paramref name="useShellExecute" /> is the pinned shell-vs-CreateProcess decision.</summary>
public interface ISetupProcessLauncher
{
    /// <summary>Starts <paramref name="executablePath" /> with <paramref name="arguments" />.</summary>
    void Launch(string executablePath, string arguments, string workingDirectory, bool useShellExecute);
}

/// <summary>
/// The two lifecycle actions a hand-off may perform. Kept as a seam so the tests pin, literally,
/// that a clean exit signals <c>ShuttingDown</c> with <c>restartRequested: false</c> and that the
/// uninstall hand-off performs neither.
/// </summary>
public interface ISetupExitFlow
{
    /// <summary>Stamps the final heartbeat as a clean shutdown. Never called with true for S6f.</summary>
    void SignalShuttingDown(bool restartRequested);

    /// <summary>Begins application shutdown.</summary>
    void Shutdown();
}

/// <summary>The production launcher: ShellExecute through <see cref="ProcessStartInfo.UseShellExecute" />.</summary>
public sealed class ShellProcessLauncher : ISetupProcessLauncher
{
    /// <inheritdoc />
    public void Launch(string executablePath, string arguments, string workingDirectory, bool useShellExecute) =>
        Process.Start(BuildStartInfo(executablePath, arguments, workingDirectory, useShellExecute))?.Dispose();

    /// <summary>
    /// The pure start-info builder — review 02 R1's last hop. This is the line pair that actually
    /// prevents review 01's rename lock: <see cref="ProcessStartInfo.WorkingDirectory" /> must be
    /// the NEUTRAL directory the caller passed, never the exe's own folder. Extracted so a test can
    /// assert it without starting a process (MUT6 pinned the old assignment and survived every
    /// gateways-level test).
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        string executablePath, string arguments, string workingDirectory, bool useShellExecute) =>
        new()
        {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = useShellExecute,
            WorkingDirectory = workingDirectory,
        };
}

/// <summary>The production exit flow over the App's heartbeat and shutdown.</summary>
public sealed class SetupExitFlow : ISetupExitFlow
{
    private readonly Action<bool> _signalShuttingDown;
    private readonly Action _shutdown;

    /// <summary>Creates the flow from the App's two actions.</summary>
    public SetupExitFlow(Action<bool> signalShuttingDown, Action shutdown)
    {
        _signalShuttingDown = signalShuttingDown ?? throw new ArgumentNullException(nameof(signalShuttingDown));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
    }

    /// <inheritdoc />
    public void SignalShuttingDown(bool restartRequested) => _signalShuttingDown(restartRequested);

    /// <inheritdoc />
    public void Shutdown() => _shutdown();
}

/// <summary>The production <see cref="ISetupGateways" /> over S6a's read-only engine pieces.</summary>
public sealed class SetupGateways : ISetupGateways
{
    /// <summary>The neutral working directory used when none is injected: the Windows folder is
    /// never under <c>Program Files\EyeCtrl</c>, so a hand-off cannot lock the folder it renames.</summary>
    private static string DefaultNeutralWorkingDirectory()
    {
        var system = Environment.SystemDirectory;
        return string.IsNullOrWhiteSpace(system) ? Path.GetTempPath() : system;
    }

    private readonly Func<string> _currentProcessPath;
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IPackageMetadataReader _metadata;
    private readonly ICatalogVerifier _catalog;
    private readonly IFileSignatureVerifier _signatures;
    private readonly IElevationPolicy _policy;
    private readonly ISetupProcessLauncher _launcher;
    private readonly ISetupExitFlow _exit;
    private readonly string _neutralWorkingDirectory;
    private readonly Action<string> _warn;

    private int _offerProbeStarted;
    private int _offerProbeDone;
    private SetupUpdateOffer? _offer;
    private bool _previousVersionProbed;
    private string? _previousVersion;
    private bool _policyProbed;
    private bool _policyBlocks;

    /// <summary>Creates the gateways. Every OS touch is a seam, so the tests never read the
    /// machine's registry, Program Files or inbox.</summary>
    /// <param name="neutralWorkingDirectory">The working directory every hand-off passes: a
    /// neutral folder, never under <c>Program Files\EyeCtrl*</c> (review 01 H1). Defaults to the
    /// Windows folder.</param>
    /// <param name="warn">Receives one machine-readable line when a probe or a launch fails; the
    /// App logs it. Never user text.</param>
    public SetupGateways(
        Func<string> currentProcessPath,
        SetupPaths paths,
        IFileSystem fileSystem,
        IPackageMetadataReader metadata,
        ICatalogVerifier catalog,
        IFileSignatureVerifier signatures,
        IElevationPolicy policy,
        ISetupProcessLauncher launcher,
        ISetupExitFlow exit,
        string? neutralWorkingDirectory = null,
        Action<string>? warn = null)
    {
        _currentProcessPath = currentProcessPath ?? throw new ArgumentNullException(nameof(currentProcessPath));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _neutralWorkingDirectory = string.IsNullOrWhiteSpace(neutralWorkingDirectory)
            ? DefaultNeutralWorkingDirectory()
            : neutralWorkingDirectory;
        _warn = warn ?? (_ => { });
    }

    /// <summary>The working directory every hand-off passes. Public so a wiring test can pin that
    /// it is neutral (never under <c>Program Files\EyeCtrl*</c>).</summary>
    public string NeutralWorkingDirectory => _neutralWorkingDirectory;

    /// <inheritdoc />
    public bool Installed => _paths.IsUnderInstallRoot(_currentProcessPath());

    /// <inheritdoc />
    public bool PreviousAvailable => _fileSystem.DirectoryExists(_paths.PreviousRoot);

    /// <inheritdoc />
    public string? PreviousVersion
    {
        get
        {
            if (!_previousVersionProbed)
            {
                _previousVersionProbed = true;
                _previousVersion = _metadata.Read(_paths.PreviousRoot).Version;
            }

            return _previousVersion;
        }
    }

    /// <inheritdoc />
    public bool PolicyBlocks
    {
        get
        {
            if (!_policyProbed)
            {
                _policyProbed = true;
                try
                {
                    _policyBlocks = !_policy.Read().Allowed;
                }
                catch (Exception ex)
                {
                    // Fail closed: an unreadable policy is not permission.
                    _policyBlocks = true;
                    _warn($"policy probe failed: {ex.GetType().Name}");
                }
            }

            return _policyBlocks;
        }
    }

    /// <inheritdoc />
    public SetupUpdateOffer? UpdateOffer => Volatile.Read(ref _offer);

    /// <inheritdoc />
    public bool UpdateOfferPending =>
        Volatile.Read(ref _offerProbeStarted) != 0 && Volatile.Read(ref _offerProbeDone) == 0;

    /// <inheritdoc />
    public void BeginUpdateOfferProbe(Action whenDone)
    {
        if (Interlocked.Exchange(ref _offerProbeStarted, 1) != 0)
            return;

        whenDone ??= static () => { };
        _ = Task.Run(() =>
        {
            SetupUpdateOffer? offer = null;
            try
            {
                offer = ComputeUpdateOffer();
            }
            catch (Exception ex)
            {
                // Review 01 N4: an IO/ACL fault on the inbox fails closed (no row), never the page.
                _warn($"update offer probe failed: {ex.GetType().Name}");
            }
            finally
            {
                Volatile.Write(ref _offer, offer);
                Volatile.Write(ref _offerProbeDone, 1);
                try
                {
                    whenDone();
                }
                catch (Exception ex)
                {
                    // The callback is caller code (a rebuild); its fault must not kill the probe.
                    _warn($"update offer probe callback failed: {ex.GetType().Name}");
                }
            }
        });
    }

    /// <inheritdoc />
    public bool StartUpdateHandoff(SetupUpdateOffer offer)
    {
        if (offer is null || !Installed)
            return false;

        // The visibility check verified the whole catalog; the hand-off re-checks the one file
        // about to be started, against the pinned signer.
        var signature = _signatures.Verify(offer.SetupExePath);
        if (!signature.Valid || !string.Equals(
                signature.SignerThumbprint, SetupConstants.PinnedThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            _warn("update hand-off refused: the package Setup's signature does not match the pinned signer");
            return false;
        }

        return LaunchAndExit(offer.SetupExePath, $"update \"{offer.Directory}\"");
    }

    /// <inheritdoc />
    public bool StartRollbackHandoff()
    {
        if (!Installed)
            return false;

        return LaunchAndExit(_paths.InstalledSetupExePath, "rollback");
    }

    /// <inheritdoc />
    public bool StartUninstallHandoff()
    {
        if (!Installed)
            return false;

        // EyeCtrl keeps running — no heartbeat, no shutdown: the App's dwell is the input for
        // Setup's own uninstall window (S6e).
        try
        {
            _launcher.Launch(
                _paths.InstalledSetupExePath, "uninstall", _neutralWorkingDirectory, useShellExecute: true);
            return true;
        }
        catch (Exception ex)
        {
            // Review 01 N3: never let a launch fault reach the dispatcher.
            _warn($"uninstall hand-off launch failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// The reviewed order (review 01 N3 / MUT2): launch FIRST; only a successful launch may signal
    /// the clean shutdown and exit. A throw keeps EyeCtrl running with nothing gone.
    /// </summary>
    private bool LaunchAndExit(string executablePath, string arguments)
    {
        try
        {
            _launcher.Launch(executablePath, arguments, _neutralWorkingDirectory, useShellExecute: true);
        }
        catch (Exception ex)
        {
            _warn($"hand-off launch failed: {ex.GetType().Name}");
            return false;
        }

        _exit.SignalShuttingDown(restartRequested: false);
        _exit.Shutdown();
        return true;
    }

    /// <summary>
    /// The production composition: OS roots from the environment, S6a's physical seams, and the
    /// pinned-thumbprint catalog verifier. <paramref name="currentProcessPath" /> is injectable so
    /// a non-bundled App.Tests host can never look "installed" by accident.
    /// </summary>
    public static SetupGateways CreateForProduction(
        ISetupExitFlow exitFlow, Func<string>? currentProcessPath = null, Action<string>? warn = null)
    {
        var paths = new SetupPaths(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var fileSystem = new PhysicalFileSystem();
        var signatures = new WinTrustFileSignatureVerifier();
        return new SetupGateways(
            currentProcessPath ?? (() => Environment.ProcessPath ?? string.Empty),
            paths,
            fileSystem,
            new PackageMetadataReader(fileSystem),
            new AuthenticodeCatalogVerifier(signatures),
            signatures,
            new ElevationPolicyGate(new WindowsRegistryStore()),
            new ShellProcessLauncher(),
            exitFlow,
            warn: warn);
    }

    /// <summary>The verification itself. Internal so the headless matrix tests exercise the
    /// decision synchronously; the page only ever reaches it through <see cref="BeginUpdateOfferProbe" />.</summary>
    internal SetupUpdateOffer? ComputeUpdateOffer()
    {
        if (!Installed)
            return null;

        var installedVersion = _metadata.Read(_paths.InstallRoot).Version;
        var newest = new InboxScanner(_paths, _fileSystem, _metadata).Newest();
        if (newest is null || !InboxScanner.IsNewerThan(newest.Metadata.Version, installedVersion))
            return null;

        var catalogPath = Path.Combine(newest.Directory, SetupConstants.CatalogName);
        var check = _catalog.Verify(catalogPath, newest.Directory, SetupConstants.PinnedThumbprint);
        if (!IsVerified(check))
            return null;

        return new SetupUpdateOffer(
            newest.Directory,
            Path.Combine(newest.Directory, SetupConstants.SetupExeName),
            newest.Metadata.Version!,
            installedVersion!);
    }

    private static bool IsVerified(CatalogVerificationResult check) =>
        check.SignatureValid
        && check.ThumbprintMatches
        && check.HashMismatches.Count == 0
        && check.MissingFiles.Count == 0
        && check.UncataloguedFiles.Count == 0
        && check.Error is null;
}
