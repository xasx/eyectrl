namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;

public class RunCopyManagerTests
{
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
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunFolder("abc"))).IsTrue();
    }
}
