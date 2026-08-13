namespace EyeCtrl.App.Tests.Startup;

using System.IO;
using System.Text.Json;
using EyeCtrl.App.Startup;

public class InstanceManifestTests
{
    private static InstanceManifest Sample() => new(
        InstanceId: "run7",
        ProcessId: 4242,
        StartedAtUtc: new DateTime(2026, 8, 13, 9, 14, 22, DateTimeKind.Utc),
        TestMode: true,
        Resources:
        [
            new InstanceResource("ProfileDirectory", true, @"C:\p\instances\run7"),
            new InstanceResource("DwellPipe", true, "eyectrl_dwell_run7")
        ],
        Refused: ["Supervision", "SelfRestart"]);

    [Fact]
    public void SerializesCamelCase_SoTheHarnessCanReadIt()
    {
        var json = JsonSerializer.Serialize(Sample(), InstanceManifest.JsonOptions);

        Check.That(json).Contains("\"instanceId\"");
        Check.That(json).Contains("\"effectiveValue\"");
        Check.That(json).Contains("\"refused\"");
        Check.That(json).Not.Contains("\"InstanceId\"");
    }

    [Fact]
    public void RoundTrips()
    {
        var json = JsonSerializer.Serialize(Sample(), InstanceManifest.JsonOptions);
        var back = JsonSerializer.Deserialize<InstanceManifest>(json, InstanceManifest.JsonOptions);

        Check.That(back).IsNotNull();
        Check.That(back!.InstanceId).IsEqualTo("run7");
        Check.That(back.Resources).HasSize(2);
        Check.That(back.Refused).ContainsExactly("Supervision", "SelfRestart");
    }

    [Fact]
    public void RoundTrips_TheScopedFlag_WhichIsWhatTheHarnessAsserts()
    {
        // The integration test's whole assertion is "no resource has scoped: false". A flag that
        // failed to round-trip would read as false for everything, or silently as true.
        var json = JsonSerializer.Serialize(
            Sample() with { Resources = [new InstanceResource("DwellPipe", false, "eyectrl_dwell")] },
            InstanceManifest.JsonOptions);
        var back = JsonSerializer.Deserialize<InstanceManifest>(json, InstanceManifest.JsonOptions);

        Check.That(back!.Resources[0].Scoped).IsFalse();
        Check.That(back.Resources[0].EffectiveValue).IsEqualTo("eyectrl_dwell");
    }

    [Fact]
    public void Write_CreatesInstanceJsonInTheProfileDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eyectrl-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            InstanceManifest.Write(dir, Sample());

            var path = Path.Combine(dir, InstanceManifest.FileName);
            Check.That(File.Exists(path)).IsTrue();
            Check.That(File.ReadAllText(path)).Contains("run7");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Write_CreatesMissingParentDirectories()
    {
        // The profile directory may not exist on a first scoped launch, so Write creates the tree
        // rather than failing. What it does NOT do is swallow other failures - see the next test.
        var nested = Path.Combine(Path.GetTempPath(), "eyectrl-missing-" + Guid.NewGuid().ToString("N"),
                                  "nested", "deeper");
        try
        {
            InstanceManifest.Write(nested, Sample());

            Check.That(File.Exists(Path.Combine(nested, InstanceManifest.FileName))).IsTrue();
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(nested))!, true); } catch { }
        }
    }

    [Fact]
    public void Write_PropagatesFailures_SoAHarnessCannotProceedUnverified()
    {
        // A "directory" path that is actually an existing FILE cannot be created, and the failure
        // must surface: a harness that cannot read instance.json must not drive real input.
        var file = Path.Combine(Path.GetTempPath(), "eyectrl-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "not a directory");
        try
        {
            Exception? ex = null;
            try { InstanceManifest.Write(file, Sample()); } catch (Exception e) { ex = e; }

            Check.That(ex).IsNotNull();
        }
        finally { try { File.Delete(file); } catch { } }
    }

    [Fact]
    public void Write_OverwritesAStaleManifest()
    {
        // A scoped profile directory can survive a previous run whose teardown failed. A manifest
        // from that run would satisfy the harness's file-exists wait while describing a dead
        // process, so the write must replace rather than append or skip.
        var dir = Path.Combine(Path.GetTempPath(), "eyectrl-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            InstanceManifest.Write(dir, Sample() with { InstanceId = "old", ProcessId = 1 });
            InstanceManifest.Write(dir, Sample());

            var text = File.ReadAllText(Path.Combine(dir, InstanceManifest.FileName));
            Check.That(text).Contains("run7");
            Check.That(text).Not.Contains("\"old\"");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void FileName_IsTheDocumentedName()
    {
        Check.That(InstanceManifest.FileName).IsEqualTo("instance.json");
    }
}
