namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;

public class RunCopyManagerTests
{
    /// <summary>
    /// S6c D1: a publish leaves exactly these WPF natives beside EyeCtrl.Setup.exe. The run copy
    /// must carry them or every handed-off window crashes with 0xC000041D/DllNotFoundException.
    /// </summary>
    private static readonly string[] ExpectedSetupNatives =
    [
        "D3DCompiler_47_cor3.dll",
        "PenImc_cor3.dll",
        "PresentationNative_cor3.dll",
        "vcruntime140_cor3.dll",
        "wpfgfx_cor3.dll",
    ];

    [Fact]
    public void ThePinnedPayload_IsExactlyTheWpfNativesTheRuntimeShips()
    {
        // The ratchet: PresentationFramework's own folder is the shared WPF runtime for this
        // test run, and a self-contained publish copies these same loose natives beside the exe.
        var frameworkDirectory = Path.GetDirectoryName(
            typeof(System.Windows.Window).Assembly.Location);
        Check.That(frameworkDirectory).IsNotNull();

        var natives = Directory.EnumerateFiles(frameworkDirectory!, "*_cor3.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Check.That(SetupPayload.RequiredCompanions
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList())
            .ContainsExactly(natives);
        Check.That(SetupPayload.RequiredCompanions).ContainsExactly(ExpectedSetupNatives);
    }

    [Fact]
    public void ARunCopy_HoldsEveryFileAPublishedSetupNeedsBesideTheExe()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");

        var result = harness.RunCopies.Prepare(harness.Paths.InstalledSetupExePath, "abc");

        Check.That(result.Ok).IsTrue();
        var files = harness.Files.FilesUnder(harness.Paths.RunFolder("abc"))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Check.That(files).ContainsExactly(
            new[] { SetupConstants.CatalogName, SetupConstants.SetupExeName }
                .Concat(ExpectedSetupNatives)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    [Fact]
    public void AMissingSetupNative_IsRefused_AndNoRunFolderIsCreated()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Files.DeleteFile(
            Path.Combine(harness.Paths.InstallRoot, "wpfgfx_cor3.dll"));

        var result = harness.RunCopies.Prepare(harness.Paths.InstalledSetupExePath, "abc");

        Check.That(result.Ok).IsFalse();
        Check.That(result.FailureReason).IsEqualTo("setupPayloadMissing:wpfgfx_cor3.dll");
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunFolder("abc"))).IsFalse();
    }

    [Fact]
    public void ANativeThatFailsTheCatalogCopiedIntoTheRunFolder_IsRefusedAndRemoved()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.CatalogMembers.Verifier = path => Path.GetFileName(path) == "wpfgfx_cor3.dll" ? 1 : 0;

        var result = harness.RunCopies.Prepare(harness.Paths.InstalledSetupExePath, "abc");

        Check.That(result.Ok).IsFalse();
        Check.That(result.FailureReason).IsEqualTo("runCopyPayload:wpfgfx_cor3.dll");
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunFolder("abc"))).IsFalse();
    }

    [Fact]
    public void AVerifiedSetup_IsCopiedIntoThePrivateRunFolder()
    {
        var harness = new SetupHarness();
        harness.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        var source = Path.Combine(@"C:\inbox\2.0.0", SetupConstants.SetupExeName);

        var result = harness.RunCopies.Prepare(source, "abc");

        Check.That(result.Ok).IsTrue();
        Check.That(result.RunCopyPath).IsEqualTo(harness.Paths.RunSetupExePath("abc"));
        Check.That(harness.Files.FileExists(result.RunCopyPath!)).IsTrue();
        Check.That(harness.Files.HasZoneIdentifier(result.RunCopyPath!)).IsFalse();
    }

    [Fact]
    public void AnInvalidSetupSignature_IsRefused_AndNothingIsCopied()
    {
        var harness = new SetupHarness();
        harness.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        harness.Signature.Result = new FileSignatureCheck(false, null, "0x800B0100");

        var result = harness.RunCopies.Prepare(
            Path.Combine(@"C:\inbox\2.0.0", SetupConstants.SetupExeName), "abc");

        Check.That(result.Ok).IsFalse();
        Check.That(result.FailureReason).Contains("setupSignature");
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunFolder("abc"))).IsFalse();
    }

    [Fact]
    public void AWrongSigner_IsRefused()
    {
        var harness = new SetupHarness();
        harness.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        harness.Signature.Result = new FileSignatureCheck(true, "DEADBEEF", null);

        var result = harness.RunCopies.Prepare(
            Path.Combine(@"C:\inbox\2.0.0", SetupConstants.SetupExeName), "abc");

        Check.That(result.FailureReason).IsEqualTo("setupThumbprint");
    }

    [Fact]
    public void AMissingSetup_IsRefused()
    {
        var harness = new SetupHarness();

        var result = harness.RunCopies.Prepare(@"C:\inbox\missing\EyeCtrl.Setup.exe", "abc");

        Check.That(result.FailureReason).IsEqualTo("setupMissing");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..\\EyeCtrl")]
    [InlineData("a.b")]
    public void AnUnsafeRunId_IsRefused(string runId)
    {
        var harness = new SetupHarness();
        harness.SeedInstalled();

        var result = harness.RunCopies.Prepare(harness.Paths.InstalledSetupExePath, runId);

        Check.That(result.FailureReason).IsEqualTo("invalidRunId");
    }

    [Fact]
    public void OldRunFolders_AreDeleted_AndTheCurrentOneIsKept()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled();
        harness.SeedVersionFolder(harness.Paths.RunFolder("old"), "0.1.0");
        harness.SeedVersionFolder(harness.Paths.RunFolder("current"), "0.1.0");

        var result = harness.RunCopies.Prepare(harness.Paths.InstalledSetupExePath, "current");

        Check.That(result.Ok).IsTrue();
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunFolder("old"))).IsFalse();
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunFolder("current"))).IsTrue();
    }

    [Fact]
    public void Uninstall_MarksTheRunFolderForDeletionAtReboot()
    {
        var harness = new SetupHarness();
        harness.SeedVersionFolder(harness.Paths.RunFolder("abc"), "1.0.0");

        harness.RunCopies.MarkForDeletionAtReboot("abc");

        Check.That(harness.Log).Contains("reboot:" + harness.Paths.RunFolder("abc"));
        Check.That(harness.Log).Contains("reboot:" + harness.Paths.RunRoot);
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunFolder("abc"))).IsTrue();
    }
}
