namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;

public class StagingServiceTests
{
    private static SetupHarness Harness() => new();

    [Fact]
    public void AValidPackage_IsCopiedIntoStaging_WithMarkOfTheWebStripped()
    {
        var harness = Harness();
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");

        var result = harness.Staging.Stage(package);

        Check.That(result.Ok).IsTrue();
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsTrue();
        var stagedFiles = harness.Files.FilesUnder(harness.Paths.StagingRoot);
        Check.That(stagedFiles).Not.IsEmpty();
        foreach (var file in stagedFiles)
            Check.That(harness.Files.HasZoneIdentifier(file)).IsFalse();
        Check.That(harness.Catalog.Calls).HasSize(1);
        Check.That(harness.Catalog.Calls[0]).Contains(SetupConstants.PinnedThumbprint);
        Check.That(harness.Files.FileExists(
            Path.Combine(package, SetupConstants.SetupExeName))).IsTrue();
        Check.That(harness.Files.HasZoneIdentifier(
            Path.Combine(package, SetupConstants.SetupExeName))).IsTrue();
    }

    [Fact]
    public void ATamperedFile_IsRefused_AndStagingIsRemoved()
    {
        var harness = Harness();
        harness.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        harness.Catalog.Result = new CatalogVerificationResult(
            true, SetupConstants.PinnedThumbprint, true,
            ["onnxruntime.dll"], [], null);

        var result = harness.Staging.Stage(@"C:\inbox\2.0.0");

        Check.That(result.Ok).IsFalse();
        Check.That(result.FailureReason).IsEqualTo(StagingFailure.Hash);
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
    }

    [Fact]
    public void AnUncataloguedFile_IsRefused_AndStagingIsRemoved()
    {
        var harness = Harness();
        harness.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        harness.Catalog.Result = new CatalogVerificationResult(
            true, SetupConstants.PinnedThumbprint, true,
            [], ["extra.dll"], null);

        var result = harness.Staging.Stage(@"C:\inbox\2.0.0");

        Check.That(result.Ok).IsFalse();
        Check.That(result.FailureReason).IsEqualTo(StagingFailure.Uncatalogued);
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
    }

    [Fact]
    public void ABrokenCatalogSignature_IsRefused()
    {
        var harness = Harness();
        harness.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        harness.Catalog.Result = new CatalogVerificationResult(
            false, null, false, [], [], "0x800B0100");

        var result = harness.Staging.Stage(@"C:\inbox\2.0.0");

        Check.That(result.FailureReason).IsEqualTo(StagingFailure.CatalogSignature);
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
    }

    [Fact]
    public void AWrongSignerThumbprint_IsRefused()
    {
        var harness = Harness();
        harness.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        harness.Catalog.Result = new CatalogVerificationResult(
            true, "DEADBEEF", false, [], [], null);

        var result = harness.Staging.Stage(@"C:\inbox\2.0.0");

        Check.That(result.FailureReason).IsEqualTo(StagingFailure.Thumbprint);
    }

    [Fact]
    public void AMissingCatalog_IsRefused()
    {
        var harness = Harness();
        harness.Files.AddFile(@"C:\inbox\2.0.0\EyeCtrl.App.exe");
        harness.Files.AddFile(@"C:\inbox\2.0.0\EyeCtrl.Setup.exe");

        var result = harness.Staging.Stage(@"C:\inbox\2.0.0");

        Check.That(result.FailureReason).IsEqualTo(StagingFailure.CatalogMissing);
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
    }

    [Fact]
    public void ACopyFailure_LeavesNoStagingFolder()
    {
        var harness = Harness();
        harness.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        harness.Files.FailCopyAt = 2;

        var result = harness.Staging.Stage(@"C:\inbox\2.0.0");

        Check.That(result.FailureReason).IsEqualTo(StagingFailure.CopyFailed);
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
    }

    [Fact]
    public void ALeftoverStagingFolder_IsReplaced()
    {
        var harness = Harness();
        harness.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        harness.SeedVersionFolder(harness.Paths.StagingRoot, "old");

        var result = harness.Staging.Stage(@"C:\inbox\2.0.0");

        Check.That(result.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.StagingRoot).Version).IsEqualTo("2.0.0");
    }
}
