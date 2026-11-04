namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// C9 S6f's gateways: the visibility matrix's underlying facts (installed; the inbox scan — no
/// inbox, older, dot-folder, unverified, newer verified, a foreign-but-valid signer, a valid
/// signature whose thumbprint does not match; the previous folder; the policy gate), the
/// hand-offs (ShellExecute with the right exe/verb/dir/NEUTRAL working directory; the update's
/// signature re-check; uninstall keeps EyeCtrl running; a throwing launch keeps it running too),
/// the reviewed launch → signal → exit order, the asynchronous catalog probe, and the clean-exit
/// pin: <c>ShuttingDown</c> with <c>restartRequested: false</c>, never a restart request.
/// </summary>
public class SetupGatewaysTests
{
    private static readonly SetupPaths Paths = new(@"C:\PFakes", @"C:\AppDataFakes", @"C:\LocalFakes");

    private const string NeutralDirectory = @"C:\Windows\System32";

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
        public List<string> Warnings { get; } = [];
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
            () => CurrentProcessPath, Paths, Fs, Metadata, Catalog, Signatures, Policy, Launcher, Exit,
            neutralWorkingDirectory: NeutralDirectory,
            warn: Warnings.Add);
    }

    private static SetupUpdateOffer OfferFor(string folder = "2.0.0") =>
        new(Path.Combine(Paths.InboxRoot, folder),
            Path.Combine(Paths.InboxRoot, folder, SetupConstants.SetupExeName),
            folder, "1.0.0");

    /// <summary>Runs the probe and waits (bounded) for its completion — the headless analogue of
    /// the window's rebuild-on-complete.</summary>
    private static SetupGateways Probed(Harness harness)
    {
        var gateways = harness.Create();
        using var done = new ManualResetEventSlim(false);
        gateways.BeginUpdateOfferProbe(done.Set);
        Check.That(done.Wait(TimeSpan.FromSeconds(10))).IsTrue();
        return gateways;
    }

    // ─── Visibility facts ────────────────────────────────────────────────

    [Fact]
    public void ADevRun_IsNotInstalled_AndNeverOffersOrHandsOff()
    {
        var harness = new Harness { CurrentProcessPath = @"C:\dev\EyeCtrl.App.exe" };
        harness.Installed().WithInboxPackage("7.0.0", "7.0.0").WithPrevious();
        harness.Catalog.Result = Verified;

        var gateways = Probed(harness);

        Check.That(gateways.Installed).IsFalse();
        Check.That(gateways.UpdateOffer).IsNull();
        Check.That(harness.Catalog.Calls).IsEmpty();

        Check.That(gateways.StartRollbackHandoff()).IsFalse();
        Check.That(gateways.StartUninstallHandoff()).IsFalse();
        Check.That(harness.Launcher.Calls).IsEmpty();
        Check.That(harness.Exit.Signals).IsEmpty();
    }

    [Fact]
    public void AnEmptyInbox_OffersNothing_WithoutRunningTheCatalogCheck()
    {
        var harness = new Harness().Installed();

        Check.That(harness.Create().ComputeUpdateOffer()).IsNull();
        Check.That(harness.Catalog.Calls).IsEmpty();
    }

    [Fact]
    public void AnOlderPackage_IsNotOffered_AndNeverReachesTheCatalogCheck()
    {
        var harness = new Harness().Installed("2.0.0").WithInboxPackage("1.0.0", "1.0.0");
        harness.Catalog.Result = Verified;

        Check.That(harness.Create().ComputeUpdateOffer()).IsNull();
        Check.That(harness.Catalog.Calls).IsEmpty();
    }

    [Fact]
    public void ADotPrefixedFolder_IsIgnored()
    {
        // S6a's scanner rule: dot-folders are editor/partial-download litter, never an offer.
        var harness = new Harness().Installed().WithInboxPackage(".7.0.0.partial", "7.0.0");
        harness.Catalog.Result = Verified;

        Check.That(harness.Create().ComputeUpdateOffer()).IsNull();
        Check.That(harness.Catalog.Calls).IsEmpty();
    }

    [Fact]
    public void AnUnverifiedPackage_IsNotOffered()
    {
        var harness = new Harness().Installed().WithInboxPackage("2.0.0", "2.0.0");
        harness.Catalog.Result = Unverified;

        Check.That(harness.Create().ComputeUpdateOffer()).IsNull();
        Check.That(harness.Catalog.Calls).HasSize(1);
    }

    [Fact]
    public void AValidlySignedCatalogWithAForeignThumbprint_IsNotOffered()
    {
        // Review 01 MUT5: valid signature, wrong signer. Without ThumbprintMatches the offer
        // would pass and the App would exit before Setup's staging check refuses it.
        var harness = new Harness().Installed().WithInboxPackage("2.0.0", "2.0.0");
        harness.Catalog.Result = new CatalogVerificationResult(
            true, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false, [], [], [], null);

        Check.That(harness.Create().ComputeUpdateOffer()).IsNull();
    }

    [Fact]
    public void ANewerVerifiedPackage_IsOffered_WithThePinnedThumbprintCheck()
    {
        var harness = new Harness().Installed("1.0.0").WithInboxPackage("2.0.0", "2.0.0");
        harness.Catalog.Result = Verified;

        var offer = harness.Create().ComputeUpdateOffer();

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

        Check.That(harness.Create().ComputeUpdateOffer()!.Version).IsEqualTo("3.0.0");
    }

    [Fact]
    public void AFreshGatewaysInstance_SeesANewlyArrivedPackage()
    {
        // Review 01 M1 at the gateway level: the App creates one instance per Settings window, so
        // an inbox that was empty on the first visit must offer on the next one.
        var harness = new Harness().Installed();
        Check.That(harness.Create().ComputeUpdateOffer()).IsNull();

        harness.WithInboxPackage("2.0.0", "2.0.0");
        harness.Catalog.Result = Verified;

        Check.That(harness.Create().ComputeUpdateOffer()).IsNotNull();
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
        Check.That(new Harness().Installed().Create().PreviousAvailable).IsFalse();
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
    public void AThrowingPolicy_Blocks()
    {
        var harness = new Harness().Installed();
        harness.Policy.Throw = true;

        Check.That(harness.Create().PolicyBlocks).IsTrue();
    }

    [Fact]
    public void AnAllowedPolicy_DoesNotBlock()
    {
        Check.That(new Harness().Installed().Create().PolicyBlocks).IsFalse();
    }

    // ─── The async probe (review 01 N2 / N4) ─────────────────────────────

    [Fact]
    public void TheProbe_ReturnsImmediately_ComputesOffThread_AndCompletesOnce()
    {
        var harness = new Harness().Installed().WithInboxPackage("2.0.0", "2.0.0");
        harness.Catalog.Result = Verified;
        using var release = new ManualResetEventSlim(false);
        harness.Catalog.BlockOn = release;

        var gateways = harness.Create();
        var completions = 0;
        gateways.BeginUpdateOfferProbe(() => Interlocked.Increment(ref completions));

        // Returned before the verification finished: nothing on the calling thread waited for it.
        Check.That(gateways.UpdateOfferPending).IsTrue();
        Check.That(gateways.UpdateOffer).IsNull();
        gateways.BeginUpdateOfferProbe(() => Interlocked.Increment(ref completions));

        release.Set();
        var clock = Stopwatch.StartNew();
        while (gateways.UpdateOfferPending && clock.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(10);

        Check.That(gateways.UpdateOfferPending).IsFalse();
        Check.That(gateways.UpdateOffer).IsNotNull();
        Check.That(completions).IsEqualTo(1);
        Check.That(harness.Catalog.Calls).HasSize(1);
    }

    [Fact]
    public void AProbeFault_FailsClosed_AndStillCompletes()
    {
        var harness = new Harness().Installed().WithInboxPackage("2.0.0", "2.0.0");
        harness.Catalog.Throw = true;

        var gateways = Probed(harness);

        Check.That(gateways.UpdateOffer).IsNull();
        Check.That(harness.Warnings).HasElementThatMatches(line => line.Contains("probe failed"));
    }

    // ─── The hand-offs ───────────────────────────────────────────────────

    [Fact]
    public void UpdateHandoff_ShellExecutesThePackagesSetup_WithTheDirectory_ThenExitsCleanly()
    {
        var harness = new Harness().Installed();
        var gateways = harness.Create();
        var offer = OfferFor();

        Check.That(gateways.StartUpdateHandoff(offer)).IsTrue();

        var launch = harness.Launcher.Calls.Single();
        Check.That(launch.Executable).IsEqualTo(offer.SetupExePath);
        Check.That(launch.Arguments).IsEqualTo($"update \"{offer.Directory}\"");
        Check.That(launch.WorkingDirectory).IsEqualTo(NeutralDirectory);
        Check.That(launch.UseShellExecute).IsTrue();

        Check.That(harness.Signatures.Calls.Single()).IsEqualTo(offer.SetupExePath);
        Check.That(harness.Exit.Signals.Single()).IsFalse();
        Check.That(harness.Exit.Shutdowns).IsEqualTo(1);
    }

    [Fact]
    public void TheHandOffOrder_IsLaunch_ThenSignal_ThenExit()
    {
        // Review 01 MUT2: signalling or exiting before the launch leaves the Supervisor idle with
        // Setup never started.
        var harness = new Harness().Installed();
        var gateways = harness.Create();
        var order = new List<string>();
        harness.Launcher.OnLaunch = () => order.Add("launch");
        harness.Exit.OnSignal = _ => order.Add("signal");
        harness.Exit.OnShutdown = () => order.Add("shutdown");

        gateways.StartUpdateHandoff(OfferFor());

        Check.That(order).HasSize(3);
        Check.That(order[0]).IsEqualTo("launch");
        Check.That(order[1]).IsEqualTo("signal");
        Check.That(order[2]).IsEqualTo("shutdown");
    }

    [Fact]
    public void UpdateHandoff_WithAnUnverifiedSetupExe_Refuses_AndKeepsRunning()
    {
        var harness = new Harness().Installed();
        harness.Signatures.Result = new FileSignatureCheck(false, null, "badSignature");
        var gateways = harness.Create();

        Check.That(gateways.StartUpdateHandoff(OfferFor())).IsFalse();

        Check.That(harness.Launcher.Calls).IsEmpty();
        Check.That(harness.Exit.Signals).IsEmpty();
        Check.That(harness.Exit.Shutdowns).IsZero();
    }

    [Fact]
    public void UpdateHandoff_WithAForeignValidSigner_Refuses_AndKeepsRunning()
    {
        // Review 01 MUT4: a valid signature by ANOTHER certificate must not be accepted.
        var harness = new Harness().Installed();
        harness.Signatures.Result = new FileSignatureCheck(
            true, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", null);
        var gateways = harness.Create();

        Check.That(gateways.StartUpdateHandoff(OfferFor())).IsFalse();
        Check.That(harness.Launcher.Calls).IsEmpty();
        Check.That(harness.Exit.Signals).IsEmpty();
    }

    [Fact]
    public void AThrowingLauncher_KeepsEyeCtrlRunning_AndWarns()
    {
        // Review 01 N3: a Win32 launch fault must never reach the dispatcher, and never exit.
        var harness = new Harness().Installed();
        harness.Launcher.Throw = new System.ComponentModel.Win32Exception(740);
        var gateways = harness.Create();

        Check.That(gateways.StartUpdateHandoff(OfferFor())).IsFalse();
        Check.That(gateways.StartRollbackHandoff()).IsFalse();
        Check.That(gateways.StartUninstallHandoff()).IsFalse();

        Check.That(harness.Exit.Signals).IsEmpty();
        Check.That(harness.Exit.Shutdowns).IsZero();
        Check.That(harness.Warnings).HasElementThatMatches(line => line.Contains("launch failed"));
    }

    [Fact]
    public void RollbackHandoff_ShellExecutesTheInstalledSetup_FromANeutralDirectory_ThenExitsCleanly()
    {
        var harness = new Harness().Installed();
        var gateways = harness.Create();

        Check.That(gateways.StartRollbackHandoff()).IsTrue();

        var launch = harness.Launcher.Calls.Single();
        Check.That(launch.Executable).IsEqualTo(Paths.InstalledSetupExePath);
        Check.That(launch.Arguments).IsEqualTo("rollback");
        Check.That(launch.WorkingDirectory).IsEqualTo(NeutralDirectory);
        Check.That(launch.UseShellExecute).IsTrue();
        // H1: the hand-off must not pin Program Files\EyeCtrl as a live cwd.
        Check.That(SetupPaths.IsUnder(Paths.InstallRoot, launch.WorkingDirectory)).IsFalse();
        Check.That(harness.Exit.Signals.Single()).IsFalse();
        Check.That(harness.Exit.Shutdowns).IsEqualTo(1);
    }

    [Fact]
    public void UninstallHandoff_ShellExecutesTheInstalledSetup_AndKeepsEyeCtrlRunning()
    {
        var harness = new Harness().Installed();
        var gateways = harness.Create();

        Check.That(gateways.StartUninstallHandoff()).IsTrue();

        var launch = harness.Launcher.Calls.Single();
        Check.That(launch.Executable).IsEqualTo(Paths.InstalledSetupExePath);
        Check.That(launch.Arguments).IsEqualTo("uninstall");
        Check.That(launch.WorkingDirectory).IsEqualTo(NeutralDirectory);
        Check.That(launch.UseShellExecute).IsTrue();
        Check.That(SetupPaths.IsUnder(Paths.InstallRoot, launch.WorkingDirectory)).IsFalse();
        Check.That(harness.Exit.Signals).IsEmpty();
        Check.That(harness.Exit.Shutdowns).IsZero();
    }

    [Fact]
    public void EveryHandoff_UsesTheSameNeutralDirectory_NeverOneInsideProgramFilesEyeCtrl()
    {
        var harness = new Harness().Installed();
        var gateways = harness.Create();

        Check.That(SetupPaths.IsUnder(Paths.InstallRoot, gateways.NeutralWorkingDirectory)).IsFalse();

        gateways.StartUpdateHandoff(OfferFor());
        gateways.StartRollbackHandoff();
        gateways.StartUninstallHandoff();

        Check.That(harness.Launcher.Calls).HasSize(3);
        Check.That(harness.Launcher.Calls).ContainsOnlyElementsThatMatch(
            call => call.WorkingDirectory == NeutralDirectory);
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
        public bool Throw { get; set; }
        public ManualResetEventSlim? BlockOn { get; set; }
        public List<(string CatalogPath, string Directory, string Thumbprint)> Calls { get; } = [];

        public CatalogVerificationResult Verify(string catalogPath, string directory, string pinnedThumbprint)
        {
            Calls.Add((catalogPath, directory, pinnedThumbprint));
            BlockOn?.Wait(TimeSpan.FromSeconds(10));
            if (Throw)
                throw new IOException("catalog unreadable");
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
        public bool Throw { get; set; }
        public int ReadCount { get; private set; }

        public ElevationPolicyResult Read()
        {
            ReadCount++;
            if (Throw)
                throw new InvalidOperationException("registry unreadable");
            return new ElevationPolicyResult(Allowed, Allowed ? 0 : 5);
        }
    }

    private sealed class FakeLauncher : ISetupProcessLauncher
    {
        public List<(string Executable, string Arguments, string WorkingDirectory, bool UseShellExecute)> Calls { get; } = [];
        public Exception? Throw { get; set; }
        public Action? OnLaunch { get; set; }

        public void Launch(string executablePath, string arguments, string workingDirectory, bool useShellExecute)
        {
            OnLaunch?.Invoke();
            if (Throw is not null)
                throw Throw;
            Calls.Add((executablePath, arguments, workingDirectory, useShellExecute));
        }
    }

    private sealed class FakeExit : ISetupExitFlow
    {
        public List<bool> Signals { get; } = [];
        public int Shutdowns { get; private set; }
        public Action<bool>? OnSignal { get; set; }
        public Action? OnShutdown { get; set; }

        public void SignalShuttingDown(bool restartRequested)
        {
            OnSignal?.Invoke(restartRequested);
            Signals.Add(restartRequested);
        }

        public void Shutdown()
        {
            OnShutdown?.Invoke();
            Shutdowns++;
        }
    }
}
