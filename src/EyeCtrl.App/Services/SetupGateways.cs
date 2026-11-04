namespace EyeCtrl.App.Services;

using System;
using System.Diagnostics;
using System.IO;
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
/// The four read-only facts are cheap directory/registry probes, except <see cref="UpdateOffer"/>:
/// its catalog verification hashes and WinTrust-checks every package file, so it is computed at
/// most once per instance (the App hands one instance to each Settings window, see
/// <see cref="CreateForProduction" />). That is the plan §10 budget: an inbox that changed while
/// Settings is open is re-checked the next time Settings opens, not mid-dwell.
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

    /// <summary>True when <c>ConsentPromptBehaviorAdmin</c> is not 0 (plan §10's gate): an update
    /// would raise a secure-desktop prompt that gaze cannot reach.</summary>
    bool PolicyBlocks { get; }

    /// <summary>
    /// The verified newer package in the inbox, or null. Uses S6a's rules: dot-folders ignored,
    /// newest by created/version/name, strictly newer than the installed version, and the whole
    /// catalog verified against the pinned thumbprint.
    /// </summary>
    SetupUpdateOffer? UpdateOffer { get; }

    /// <summary>
    /// Hand off to the offered package's Setup: re-check the Setup exe's signature against the
    /// pinned thumbprint (belt and braces after the catalog check), start
    /// <c>EyeCtrl.Setup.exe update "&lt;dir&gt;"</c> with ShellExecute, then exit cleanly — the
    /// lifecycle is <see cref="ISetupExitFlow" />, which must never set the restart flag.
    /// </summary>
    void StartUpdateHandoff(SetupUpdateOffer offer);

    /// <summary>Start the installed Setup's <c>rollback</c> with ShellExecute, then exit cleanly.</summary>
    void StartRollbackHandoff();

    /// <summary>Start the installed Setup's <c>uninstall</c> with ShellExecute. EyeCtrl keeps
    /// running: its dwell is the input for Setup's own uninstall window.</summary>
    void StartUninstallHandoff();
}

/// <summary>Starts a process. <paramref name="useShellExecute" /> is the pinned shell-vs-
/// CreateProcess decision for S6f's hand-off — the fake launcher records it, and the
/// CreateProcess mutant fails the hand-off test.</summary>
public interface ISetupProcessLauncher
{
    /// <summary>Starts <paramref name="executablePath" /> with <paramref name="arguments" />.</summary>
    void Launch(string executablePath, string arguments, bool useShellExecute);
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
    public void Launch(string executablePath, string arguments, bool useShellExecute) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = useShellExecute,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
        })?.Dispose();
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
    private readonly Func<string> _currentProcessPath;
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IPackageMetadataReader _metadata;
    private readonly ICatalogVerifier _catalog;
    private readonly IFileSignatureVerifier _signatures;
    private readonly IElevationPolicy _policy;
    private readonly ISetupProcessLauncher _launcher;
    private readonly ISetupExitFlow _exit;

    private bool _offerProbed;
    private SetupUpdateOffer? _offer;
    private bool _previousVersionProbed;
    private string? _previousVersion;
    private bool _policyProbed;
    private bool _policyBlocks;

    /// <summary>Creates the gateways. Every OS touch is a seam, so the tests never read the
    /// machine's registry, Program Files or inbox.</summary>
    public SetupGateways(
        Func<string> currentProcessPath,
        SetupPaths paths,
        IFileSystem fileSystem,
        IPackageMetadataReader metadata,
        ICatalogVerifier catalog,
        IFileSignatureVerifier signatures,
        IElevationPolicy policy,
        ISetupProcessLauncher launcher,
        ISetupExitFlow exit)
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
    }

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
                _policyBlocks = !_policy.Read().Allowed;
            }

            return _policyBlocks;
        }
    }

    /// <inheritdoc />
    public SetupUpdateOffer? UpdateOffer
    {
        get
        {
            if (!_offerProbed)
            {
                _offerProbed = true;
                _offer = ComputeUpdateOffer();
            }

            return _offer;
        }
    }

    /// <inheritdoc />
    public void StartUpdateHandoff(SetupUpdateOffer offer)
    {
        if (offer is null || !Installed)
            return;

        // The visibility check verified the whole catalog; the hand-off re-checks the one file
        // about to be started, against the pinned signer. A failure here refuses silently (the
        // recorded S6f minor): it can only happen when the inbox changed between the page build
        // and the dwell, and Setup re-verifies everything itself before installing.
        var signature = _signatures.Verify(offer.SetupExePath);
        if (!signature.Valid || !string.Equals(
                signature.SignerThumbprint, SetupConstants.PinnedThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _launcher.Launch(offer.SetupExePath, $"update \"{offer.Directory}\"", useShellExecute: true);
        _exit.SignalShuttingDown(restartRequested: false);
        _exit.Shutdown();
    }

    /// <inheritdoc />
    public void StartRollbackHandoff()
    {
        if (!Installed)
            return;

        _launcher.Launch(_paths.InstalledSetupExePath, "rollback", useShellExecute: true);
        _exit.SignalShuttingDown(restartRequested: false);
        _exit.Shutdown();
    }

    /// <inheritdoc />
    public void StartUninstallHandoff()
    {
        if (!Installed)
            return;

        // EyeCtrl keeps running — no heartbeat, no shutdown: the App's dwell is the input for
        // Setup's own uninstall window (S6e).
        _launcher.Launch(_paths.InstalledSetupExePath, "uninstall", useShellExecute: true);
    }

    /// <summary>
    /// The production composition: OS roots from the environment, S6a's physical seams, and the
    /// pinned-thumbprint catalog verifier. <paramref name="currentProcessPath" /> is injectable so
    /// a non-bundled App.Tests host can never look "installed" by accident.
    /// </summary>
    public static SetupGateways CreateForProduction(ISetupExitFlow exitFlow, Func<string>? currentProcessPath = null)
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
            exitFlow);
    }

    private SetupUpdateOffer? ComputeUpdateOffer()
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
