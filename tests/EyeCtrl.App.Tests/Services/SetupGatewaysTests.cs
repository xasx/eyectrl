namespace EyeCtrl.App.Tests.Services;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// C9 S6f's gateways: the visibility matrix's underlying facts (installed; the inbox scan — no
/// inbox, older, dot-folder, unverified, newer verified; the previous folder; the policy gate),
/// the hand-offs (ShellExecute with the right exe/verb/dir; the update's signature re-check;
/// uninstall keeps EyeCtrl running), and the clean-exit pin: <c>ShuttingDown</c> with
/// <c>restartRequested: false</c>, never a restart request.
/// </summary>
public class SetupGatewaysTests
{
    private static readonly SetupPaths Paths = new(@"C:\PFakes", @"C:\AppDataFakes", @"C:\LocalFakes");

    private static readonly CatalogVerificationResult Verified =
        new(true, SetupConstants.PinnedThumbprint, true, [], [], [], null);

    private static readonly CatalogVerificationResult Unverified =
        new(false, null, false, [], [], [], "signed");

    private sealed class Harness
    {
        public InMemoryFileSystem Fs { get; } = new();
        public FakeMetadata Metadata { get; } = new();
        public FakeCatalog Catalog { get; } = new();
        public FakeSignatures Signatures { get; } = new();
        public FakePolicy Policy { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public FakeExit Exit { get; } = new();
        public string CurrentProcessPath { get; set; } = @"C:\PFakes\EyeCtrl\EyeCtrl.App.exe";

        public Harness Installed(string version = "1.0.0")
        {
            Metadata.ByDirectory[Paths.InstallRoot] = new PackageMetadata(version, null, null);
            return this;
        }

        public Harness WithPrevious(string version = "0.9.0")
        {
            Fs.Directories.Add(Paths.PreviousRoot);
            Metadata.ByDirectory[Paths.PreviousRoot] = new PackageMetadata(version, null, null);
            return this;
        }

        public Harness WithInboxPackage(string folder, string version, DateTimeOffset? created = null)
        {
            Fs.Directories.Add(Paths.InboxRoot);
            var directory = Path.Combine(Paths.InboxRoot, folder);
            Fs.Directories.Add(directory);
            Metadata.ByDirectory[directory] = new PackageMetadata(
                version, null, created ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            return this;
        }

        public SetupGateways Create() => new(
            () => CurrentProcessPath, Paths, Fs, Metadata, Catalog, Signatures, Policy, Launcher, Exit);
    }

    private static SetupUpdateOffer OfferFor(string folder = "2.0.0") =>
        new(Path.Combine(Paths.InboxRoot, folder),
            Path.Combine(Paths.InboxRoot, folder, SetupConstants.SetupExeName),
            folder, "1.0.0");

    // ─── Visibility facts ────────────────────────────────────────────────

    [Fact]
    public void ADevRun_IsNotInstalled_AndNeverOffersOrHandsOff()
    {
        var harness = new Harness { CurrentProcessPath = @"C:\dev\EyeCtrl.App.exe" };
        harness.Installed().WithInboxPackage("7.0.0", "7.0.0").WithPrevious();
        harness.Catalog.Result = Verified;

        var gateways = harness.Create();

        Check.That(gateways.Installed).IsFalse();
        Check.That(gateways.UpdateOffer).IsNull();
        Check.That(harness.Catalog.Calls).IsEmpty();

        gateways.StartRollbackHandoff();
        gateways.StartUninstallHandoff();
        Check.That(harness.Launcher.Calls).IsEmpty();
        Check.That(harness.Exit.Signals).IsEmpty();
    }

    [Fact]
    public void AnEmptyInbox_OffersNothing_WithoutRunningTheCatalogCheck()
    {
        var harness = new Harness().Installed();
        var gateways = harness.Create();

        Check.That(gateways.Installed).IsTrue();
        Check.That(gateways.UpdateOffer).IsNull();
        Check.That(harness.Catalog.Calls).IsEmpty();
    }

    [Fact]
    public void AnOlderPackage_IsNotOffered_AndNeverReachesTheCatalogCheck()
    {
        var harness = new Harness().Installed("2.0.0").WithInboxPackage("1.0.0", "1.0.0");
        harness.Catalog.Result = Verified;

        Check.That(harness.Create().UpdateOffer).IsNull();
        Check.That(harness.Catalog.Calls).IsEmpty();
    }

    [Fact]
    public void ADotPrefixedFolder_IsIgnored()
    {
        // S6a's scanner rule: dot-folders are editor/partial-download litter, never an offer.
        var harness = new Harness().Installed().WithInboxPackage(".7.0.0.partial", "7.0.0");
        harness.Catalog.Result = Verified;

        Check.That(harness.Create().UpdateOffer).IsNull();
        Check.That(harness.Catalog.Calls).IsEmpty();
    }

    [Fact]
    public void AnUnverifiedPackage_IsNotOffered()
    {
        var harness = new Harness().Installed().WithInboxPackage("2.0.0", "2.0.0");
        harness.Catalog.Result = Unverified;

        Check.That(harness.Create().UpdateOffer).IsNull();
        Check.That(harness.Catalog.Calls).HasSize(1);
    }

    [Fact]
    public void ANewerVerifiedPackage_IsOffered_WithThePinnedThumbprintCheck()
    {
        var harness = new Harness().Installed("1.0.0").WithInboxPackage("2.0.0", "2.0.0");
        harness.Catalog.Result = Verified;

        var offer = harness.Create().UpdateOffer;

        Check.That(offer).IsNotNull();
        var directory = Path.Combine(Paths.InboxRoot, "2.0.0");
        Check.That(offer!.Directory).IsEqualTo(directory);
        Check.That(offer.SetupExePath).IsEqualTo(Path.Combine(directory, SetupConstants.SetupExeName));
        Check.That(offer.Version).IsEqualTo("2.0.0");
        Check.That(offer.InstalledVersion).IsEqualTo("1.0.0");

        var call = harness.Catalog.Calls.Single();
        Check.That(call.CatalogPath).IsEqualTo(Path.Combine(directory, SetupConstants.CatalogName));
        Check.That(call.Directory).IsEqualTo(directory);
        Check.That(call.Thumbprint).IsEqualTo(SetupConstants.PinnedThumbprint);
    }

    [Fact]
    public void TheNewestPackage_IsTheOneOffered()
    {
        var harness = new Harness().Installed("1.0.0")
            .WithInboxPackage("2.0.0", "2.0.0", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
            .WithInboxPackage("3.0.0", "3.0.0", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        harness.Catalog.Result = Verified;

        Check.That(harness.Create().UpdateOffer!.Version).IsEqualTo("3.0.0");
    }

    [Fact]
    public void TheCatalogCheck_RunsAtMostOncePerInstance()
    {
        // The page rebuilds invoke the fact repeatedly; the WinTrust+SIP pass must not repeat.
        var harness = new Harness().Installed().WithInboxPackage("2.0.0", "2.0.0");
        harness.Catalog.Result = Verified;
        var gateways = harness.Create();

        _ = gateways.UpdateOffer;
        _ = gateways.UpdateOffer;
        _ = gateways.UpdateOffer;

        Check.That(harness.Catalog.Calls).HasSize(1);
    }

    [Fact]
    public void ThePreviousFolder_IsItsOwnFact_WithItsVersion()
    {
        var harness = new Harness().Installed().WithPrevious("0.9.0");
        var gateways = harness.Create();

        Check.That(gateways.PreviousAvailable).IsTrue();
        Check.That(gateways.PreviousVersion).IsEqualTo("0.9.0");
    }

    [Fact]
    public void WithoutThePreviousFolder_ThereIsNothingToRollBackTo()
    {
        var gateways = new Harness().Installed().Create();

        Check.That(gateways.PreviousAvailable).IsFalse();
    }

    [Fact]
    public void ThePolicyGate_FailsClosed_AndIsReadOnce()
    {
        var harness = new Harness().Installed();
        harness.Policy.Allowed = false;
        var gateways = harness.Create();

        Check.That(gateways.PolicyBlocks).IsTrue();
        _ = gateways.PolicyBlocks;
        Check.That(harness.Policy.ReadCount).IsEqualTo(1);
    }

    [Fact]
    public void AnAllowedPolicy_DoesNotBlock()
    {
        Check.That(new Harness().Installed().Create().PolicyBlocks).IsFalse();
    }

    // ─── The hand-offs ───────────────────────────────────────────────────

    [Fact]
    public void UpdateHandoff_ShellExecutesThePackagesSetup_WithTheDirectory_ThenExitsCleanly()
    {
        var harness = new Harness().Installed();
        var gateways = harness.Create();
        var offer = OfferFor();

        gateways.StartUpdateHandoff(offer);

        var launch = harness.Launcher.Calls.Single();
        Check.That(launch.Executable).IsEqualTo(offer.SetupExePath);
        Check.That(launch.Arguments).IsEqualTo($"update \"{offer.Directory}\"");
        Check.That(launch.UseShellExecute).IsTrue();

        Check.That(harness.Signatures.Calls.Single()).IsEqualTo(offer.SetupExePath);
        Check.That(harness.Exit.Signals.Single()).IsFalse();
        Check.That(harness.Exit.Shutdowns).IsEqualTo(1);
    }

    [Fact]
    public void UpdateHandoff_WithAnUnverifiedSetupExe_Refuses_AndKeepsRunning()
    {
        var harness = new Harness().Installed();
        harness.Signatures.Result = new FileSignatureCheck(false, null, "badSignature");
        var gateways = harness.Create();

        gateways.StartUpdateHandoff(OfferFor());

        Check.That(harness.Launcher.Calls).IsEmpty();
        Check.That(harness.Exit.Signals).IsEmpty();
        Check.That(harness.Exit.Shutdowns).IsZero();
    }

    [Fact]
    public void RollbackHandoff_ShellExecutesTheInstalledSetup_ThenExitsCleanly()
    {
        var harness = new Harness().Installed();
        var gateways = harness.Create();

        gateways.StartRollbackHandoff();

        var launch = harness.Launcher.Calls.Single();
        Check.That(launch.Executable).IsEqualTo(Paths.InstalledSetupExePath);
        Check.That(launch.Arguments).IsEqualTo("rollback");
        Check.That(launch.UseShellExecute).IsTrue();
        Check.That(harness.Exit.Signals.Single()).IsFalse();
        Check.That(harness.Exit.Shutdowns).IsEqualTo(1);
    }

    [Fact]
    public void UninstallHandoff_ShellExecutesTheInstalledSetup_AndKeepsEyeCtrlRunning()
    {
        var harness = new Harness().Installed();
        var gateways = harness.Create();

        gateways.StartUninstallHandoff();

        var launch = harness.Launcher.Calls.Single();
        Check.That(launch.Executable).IsEqualTo(Paths.InstalledSetupExePath);
        Check.That(launch.Arguments).IsEqualTo("uninstall");
        Check.That(launch.UseShellExecute).IsTrue();
        Check.That(harness.Exit.Signals).IsEmpty();
        Check.That(harness.Exit.Shutdowns).IsZero();
    }

    [Fact]
    public void NoHandoff_EverRequestsARestart()
    {
        // The ticket's pin: a clean exit carries ShuttingDown and NEVER RestartRequested,
        // so the Supervisor goes idle instead of relaunching.
        var harness = new Harness().Installed();
        var gateways = harness.Create();

        gateways.StartUpdateHandoff(OfferFor());
        gateways.StartRollbackHandoff();
        gateways.StartUninstallHandoff();

        Check.That(harness.Exit.Signals).HasSize(2);
        Check.That(harness.Exit.Signals).ContainsOnlyElementsThatMatch(restartRequested => !restartRequested);
    }

    [Fact]
    public void TheCleanExit_PinsShuttingDownWithoutRestart_OnTheRealPublisher()
    {
        // Supervision-on shape, end to end at the App side of the wire: the payload the
        // Supervisor reads is ShuttingDown=true, RestartRequested=false — its "go idle" branch.
        using var publisher = new HeartbeatPublisher(
            supervised: false, pipeName: "eyectrl_watch_test_" + Guid.NewGuid().ToString("N"));
        var flow = new SetupExitFlow(publisher.SignalShuttingDown, () => { });

        flow.SignalShuttingDown(restartRequested: false);

        Check.That(publisher.CurrentPayload).IsNotNull();
        Check.That(publisher.CurrentPayload!.ShuttingDown).IsTrue();
        Check.That(publisher.CurrentPayload.RestartRequested).IsFalse();
    }

    // ─── Fakes ───────────────────────────────────────────────────────────

    private sealed class InMemoryFileSystem : IFileSystem
    {
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => Files.Contains(path);
        public bool DirectoryExists(string path) => Directories.Contains(path);

        public IReadOnlyList<string> EnumerateDirectories(string directory) =>
            Directories
                .Where(d => string.Equals(Path.GetDirectoryName(d), directory, StringComparison.OrdinalIgnoreCase))
                .ToList();

        public IReadOnlyList<string> EnumerateFiles(string directory, bool recursive) =>
            throw new NotSupportedException();

        public void CreateDirectory(string path) => throw new NotSupportedException();
        public void DeleteFile(string path) => throw new NotSupportedException();
        public void DeleteDirectory(string path, bool recursive) => throw new NotSupportedException();
        public void MoveDirectory(string source, string destination) => throw new NotSupportedException();
        public void CopyFile(string source, string destination, bool stripZoneIdentifier) => throw new NotSupportedException();
        public void CopyDirectory(string source, string destination, bool stripZoneIdentifier) => throw new NotSupportedException();
        public string ReadAllText(string path) => throw new NotSupportedException();
        public void WriteAllText(string path, string contents) => throw new NotSupportedException();
        public void MarkForDeletionAtReboot(string path) => throw new NotSupportedException();
    }

    private sealed class FakeMetadata : IPackageMetadataReader
    {
        public Dictionary<string, PackageMetadata> ByDirectory { get; } = new(StringComparer.OrdinalIgnoreCase);

        public PackageMetadata Read(string directory) =>
            ByDirectory.TryGetValue(directory, out var value) ? value : PackageMetadata.Empty;
    }

    private sealed class FakeCatalog : ICatalogVerifier
    {
        public CatalogVerificationResult Result { get; set; } = Unverified;
        public List<(string CatalogPath, string Directory, string Thumbprint)> Calls { get; } = [];

        public CatalogVerificationResult Verify(string catalogPath, string directory, string pinnedThumbprint)
        {
            Calls.Add((catalogPath, directory, pinnedThumbprint));
            return Result;
        }
    }

    private sealed class FakeSignatures : IFileSignatureVerifier
    {
        public FileSignatureCheck Result { get; set; } = new(true, SetupConstants.PinnedThumbprint, null);
        public List<string> Calls { get; } = [];

        public FileSignatureCheck Verify(string filePath)
        {
            Calls.Add(filePath);
            return Result;
        }
    }

    private sealed class FakePolicy : IElevationPolicy
    {
        public bool Allowed { get; set; } = true;
        public int ReadCount { get; private set; }

        public ElevationPolicyResult Read()
        {
            ReadCount++;
            return new ElevationPolicyResult(Allowed, Allowed ? 0 : 5);
        }
    }

    private sealed class FakeLauncher : ISetupProcessLauncher
    {
        public List<(string Executable, string Arguments, bool UseShellExecute)> Calls { get; } = [];

        public void Launch(string executablePath, string arguments, bool useShellExecute) =>
            Calls.Add((executablePath, arguments, useShellExecute));
    }

    private sealed class FakeExit : ISetupExitFlow
    {
        public List<bool> Signals { get; } = [];
        public int Shutdowns { get; private set; }

        public void SignalShuttingDown(bool restartRequested) => Signals.Add(restartRequested);
        public void Shutdown() => Shutdowns++;
    }
}
