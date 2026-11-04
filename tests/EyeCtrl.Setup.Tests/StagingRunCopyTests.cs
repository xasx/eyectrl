namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Tests.TestSupport;

/// <summary>F7: the run copy comes from the verified staging folder and is itself re-verified.</summary>
public class StagingRunCopyTests
{
    [Fact]
    public void AStagingFailure_LeavesNoRunCopy()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        harness.Catalog.Result = new CatalogVerificationResult(
            true, SetupConstants.PinnedThumbprint, true, ["onnxruntime.dll"], [], [], null);

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.VerifyFailed);
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunFolder("run1"))).IsFalse();
    }

    [Fact]
    public void TheRunCopy_IsTakenFromStaging_AfterVerification()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        var staged = Path.Combine(harness.Paths.StagingRoot, SetupConstants.SetupExeName);
        var run = harness.Paths.RunSetupExePath("run1");
        Check.That(harness.Log.Any(entry =>
            entry.StartsWith("copy:" + staged + "->" + run, StringComparison.OrdinalIgnoreCase))).IsTrue();
        // Staging happens before the run copy copy.
        var copyDir = harness.Log.FindIndex(entry =>
            entry.StartsWith("copyDir:", StringComparison.OrdinalIgnoreCase));
        var copyRun = harness.Log.FindIndex(entry =>
            entry.StartsWith("copy:" + staged, StringComparison.OrdinalIgnoreCase));
        Check.That(copyDir).IsStrictlyGreaterThan(-1);
        Check.That(copyRun).IsStrictlyGreaterThan(copyDir);
    }

    [Fact]
    public void ATamperedRunCopy_IsRefusedAndRemoved()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        harness.Signature.Verifier = path =>
            path.Contains("EyeCtrl.run", StringComparison.OrdinalIgnoreCase)
                ? new FileSignatureCheck(false, SetupConstants.PinnedThumbprint, "tampered")
                : new FileSignatureCheck(true, SetupConstants.PinnedThumbprint, null);

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.VerifyFailed);
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunFolder("run1"))).IsFalse();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
    }
}
