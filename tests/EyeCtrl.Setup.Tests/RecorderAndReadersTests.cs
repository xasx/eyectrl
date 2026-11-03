namespace EyeCtrl.Setup.Tests;

using System.Text.Json;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Tests.TestSupport;

public class ResultRecorderTests
{
    [Fact]
    public void AResult_RoundTripsThroughSetupLastJson()
    {
        var harness = new SetupHarness();
        var result = new SetupResult
        {
            Verb = SetupVerb.Update,
            Outcome = SetupOutcome.RolledBack,
            Version = "2.0.0",
            PreviousVersion = "1.0.0",
            Time = new DateTimeOffset(2026, 11, 3, 12, 34, 56, TimeSpan.Zero),
            Detail = "newVersionFailed:missingTokenLine",
        };

        harness.Recorder.Write(result);

        var raw = harness.Files.Contents(harness.Paths.RecordPath);
        Check.That(raw).IsNotNull();
        Check.That(raw!).Contains("\"outcome\": \"rolledBack\"");
        Check.That(raw!).Contains("\"version\": \"2.0.0\"");
        Check.That(raw!).Contains("\"previousVersion\": \"1.0.0\"");
        var read = harness.Recorder.TryReadLast();
        Check.That(read).IsNotNull();
        Check.That(read!.Verb).IsEqualTo(SetupVerb.Update);
        Check.That(read.Outcome).IsEqualTo(SetupOutcome.RolledBack);
        Check.That(read.Version).IsEqualTo("2.0.0");
        Check.That(read.Detail).IsEqualTo("newVersionFailed:missingTokenLine");
    }

    [Fact]
    public void AMissingOrCorruptRecord_ReadsAsNull()
    {
        var harness = new SetupHarness();
        Check.That(harness.Recorder.TryReadLast()).IsNull();

        harness.Files.AddFile(harness.Paths.RecordPath, "{ not json");
        Check.That(harness.Recorder.TryReadLast()).IsNull();
    }

    [Fact]
    public void TheRecordPath_IsTheOnlyFileWritten()
    {
        var harness = new SetupHarness();
        harness.Recorder.Write(new SetupResult
        {
            Verb = SetupVerb.Repair,
            Outcome = SetupOutcome.Repaired,
            Time = harness.Clock.UtcNow,
        });

        Check.That(harness.Files.FileExists(harness.Paths.RecordPath)).IsTrue();
        Check.That(harness.Files.FilesUnder(harness.Paths.AppDataRoot)).HasSize(1);
    }
}

public class PackageAndSettingsReaderTests
{
    [Fact]
    public void PackageMetadata_ReadsVersionShaAndCreated()
    {
        var harness = new SetupHarness();

        var metadata = harness.Metadata.Read(Path.GetDirectoryName(
            Path.Combine(harness.Paths.InstallRoot, "package.json"))!);

        Check.That(metadata).IsEqualTo(PackageMetadata.Empty);
        harness.SeedInstalled("3.1.4");
        metadata = harness.Metadata.Read(harness.Paths.InstallRoot);
        Check.That(metadata.Version).IsEqualTo("3.1.4");
        Check.That(metadata.Sha).IsEqualTo("abcdef0");
        Check.That(metadata.Created).IsEqualTo(DateTimeOffset.Parse("2026-11-03T10:00:00Z"));
    }

    [Fact]
    public void PackageMetadata_IsTolerantOfAMalformedFile()
    {
        var harness = new SetupHarness();
        harness.Files.AddFile(
            Path.Combine(harness.Paths.InstallRoot, "package.json"), "{ broken");

        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot)).IsEqualTo(PackageMetadata.Empty);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SettingsSnapshot_ReadsBothSwitches(bool supervision, bool startOnBoot)
    {
        var harness = new SetupHarness();
        var json = "{\"supervision\":{\"enabled\":" + (supervision ? "true" : "false")
            + "},\"general\":{\"startOnBoot\":" + (startOnBoot ? "true" : "false") + "}}";
        harness.Files.AddFile(harness.Paths.SettingsPath, json);

        var state = new EyeCtrl.Setup.Install.SettingsSnapshotReader(
            harness.Paths, harness.Files).Read();

        Check.That(state.SupervisionEnabled).IsEqualTo(supervision);
        Check.That(state.StartOnBoot).IsEqualTo(startOnBoot);
    }

    [Fact]
    public void SettingsSnapshot_DefaultsToBothOff()
    {
        var harness = new SetupHarness();
        var state = new EyeCtrl.Setup.Install.SettingsSnapshotReader(
            harness.Paths, harness.Files).Read();

        Check.That(state).IsEqualTo(default(SupervisionBootState));
    }
}
