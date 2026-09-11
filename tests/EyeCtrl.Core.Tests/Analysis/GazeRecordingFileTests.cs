namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Benchmarks;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// The seam between the two Stage-2 tickets: the analyzer must read what the recorder actually
/// writes. A round-trip through a real <see cref="GazeRecorder"/> is the only check that keeps the
/// two from drifting into incompatible formats — the failure that would only surface after a
/// measurement session on the physical device, when the data is expensive to recollect.
/// </summary>
public class GazeRecordingFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eyectrl-recfile-" + Guid.NewGuid().ToString("N"));

    private string Recording => Path.Combine(_dir, "gaze-recording.csv");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static GazeRecordingHeader Header() => new(
        ProviderType: "StreamEngine",
        ScreenWidthPhysical: 3840,
        ScreenHeightPhysical: 2400,
        CalibrationActive: true,
        CalibrationVersion: "2026-09-11T10:00:00.0000000Z",
        SettingsVersion: 3);

    [Fact]
    public void RoundTripsARecordedStream_SamplesAndHeaderIntact()
    {
        var written = GazeStreamGenerator.GenerateWithDropout();

        using (var recorder = new GazeRecorder(Recording, Header(), capacity: 4096))
        {
            foreach (var sample in written) recorder.Record(sample);
        }   // Dispose drains

        var read = GazeRecordingFile.Read(Recording);

        Check.That(read.MalformedLines).IsEqualTo(0);
        Check.That(read.Samples).HasSize(written.Length);
        Check.That(read.Header.ProviderType).IsEqualTo("StreamEngine");
        Check.That(read.Header.CalibrationActive).IsTrue();
        Check.That(read.Header.SettingsVersion).IsEqualTo(3L);

        // Exact, not approximate: the recorder writes round-trip ("R") formatting precisely so the
        // analyzer measures the samples the device produced rather than a rounded copy of them.
        for (int i = 0; i < written.Length; i++)
        {
            Check.That(read.Samples[i].X).IsEqualTo(written[i].X);
            Check.That(read.Samples[i].Y).IsEqualTo(written[i].Y);
            Check.That(read.Samples[i].TimestampMicroseconds).IsEqualTo(written[i].TimestampMicroseconds);
            Check.That(read.Samples[i].IsValid).IsEqualTo(written[i].IsValid);
        }
    }

    [Fact]
    public void ARecordedStreamCanBeReplayedAndMeasured()
    {
        // End to end: record → read → replay → metrics, the path a real session will take.
        using (var recorder = new GazeRecorder(Recording, Header(), capacity: 4096))
        {
            foreach (var sample in GazeStreamGenerator.GenerateWithDropout()) recorder.Record(sample);
        }

        var read = GazeRecordingFile.Read(Recording);
        var frames = ReplayPipeline.Run(read.Samples, ReplayPipeline.Baseline);
        var shape = GazeMetrics.MeasureStream(read.Samples);

        Check.That(frames).HasSize(read.Samples.Count);
        Check.That(shape.ShortGapCount).IsEqualTo(1);
        Check.That(shape.LongGapCount).IsEqualTo(1);
    }

    [Fact]
    public void CountsMalformedLines_RatherThanSilentlyDroppingThem()
    {
        // A truncated file must be visible as truncated. Quietly skipping bad lines would let a partial
        // recording be measured as if it were whole.
        var parsed = GazeRecordingFile.Parse(new[]
        {
            "# {\"providerType\":\"Mouse\",\"screenWidthPhysical\":1920,\"screenHeightPhysical\":1080," +
            "\"calibrationActive\":false,\"calibrationVersion\":\"\",\"settingsVersion\":0}",
            "1,2,1000,1",
            "not,a,sample",
            "3,4",
            "5,6,2000,0",
        });

        Check.That(parsed.Samples).HasSize(2);
        Check.That(parsed.MalformedLines).IsEqualTo(2);
        Check.That(parsed.Header.ProviderType).IsEqualTo("Mouse");
    }

    [Fact]
    public void IgnoresTheRepeatedHeaderOfARotatedFile()
    {
        var parsed = GazeRecordingFile.Parse(new[]
        {
            "# {\"providerType\":\"StreamEngine\",\"settingsVersion\":1}",
            "1,2,1000,1",
            "# {\"providerType\":\"Mouse\",\"settingsVersion\":9}",
            "3,4,2000,1",
        });

        Check.That(parsed.Samples).HasSize(2);
        Check.That(parsed.Header.ProviderType).IsEqualTo("StreamEngine");   // the first one describes the session
        Check.That(parsed.MalformedLines).IsEqualTo(0);
    }

    [Fact]
    public void ParsesInvariantCultureRegardlessOfTheCurrentCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var parsed = GazeRecordingFile.Parse(new[] { "1234.5,987.25,5000,1" });

            Check.That(parsed.MalformedLines).IsEqualTo(0);
            Check.That(parsed.Samples[0].X).IsEqualTo(1234.5);
            Check.That(parsed.Samples[0].Y).IsEqualTo(987.25);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
