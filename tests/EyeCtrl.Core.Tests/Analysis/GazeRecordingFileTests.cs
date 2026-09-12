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

    // A calibrated session, transform included — which is what a real recorded session looks like, and
    // what the analyzer now insists on: CalibrationActive with no coefficients is refused, because
    // replay would otherwise measure the uncorrected stream and call it the user's experience.
    private static GazeRecordingHeader Header() => new(
        ProviderType: "StreamEngine",
        ScreenWidthPhysical: 3840,
        ScreenHeightPhysical: 2400,
        CalibrationActive: true,
        CalibrationVersion: "2026-09-11T10:00:00.0000000Z",
        SettingsVersion: 3,
        CalibrationTransform: new AffineTransformParameters(1.02, 0.01, -6.5, -0.01, 0.98, 3.25));

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

        // The transform round-trips too, exactly: replay reapplies these coefficients, so a rounded copy
        // would shift every replayed position by an amount that grows with the screen coordinate.
        Check.That(read.Header.CalibrationTransform).IsNotNull();
        Check.That(read.Header.CalibrationTransform!.Value)
             .IsEqualTo(new AffineTransformParameters(1.02, 0.01, -6.5, -0.01, 0.98, 3.25));

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

        // The RECORDING overload, not the raw one. This test claims to be the path a real session
        // takes, so it has to take the path a real session takes: the raw overload skips both the
        // usability gate and the session's calibration transform, and this is the call the next
        // person analysing a real file will copy.
        var frames = ReplayPipeline.Run(read, ReplayPipeline.Baseline);
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

    // ── The dropped-sample safety net ─────────────────────────────────────────

    [Fact]
    public void RoundTripsTheTrailer_SoTheDropCountSurvivesToTheAnalyzer()
    {
        // The seam that makes the whole safety net work: the recorder writes a count and the analyzer
        // reads the same count. Anything less and check #11 of the operator protocol is unperformable.
        using (var recorder = new GazeRecorder(Recording, Header(), capacity: 4096))
        {
            foreach (var sample in GazeStreamGenerator.GenerateWithDropout()) recorder.Record(sample);
        }

        var read = GazeRecordingFile.Read(Recording);

        Check.That(read.Trailer).IsNotNull();
        Check.That(read.Trailer!.Value.DroppedSamples).IsEqualTo(0L);
        Check.That(read.Trailer.Value.RecordedSamples).IsEqualTo((long)read.Samples.Count);
        Check.That(read.UnusableReason).IsNull();
    }

    [Fact]
    public void ARecordingThatDroppedSamples_IsRefused()
    {
        // The finding this exists for. Dropped samples leave a hole that is indistinguishable from a
        // tracker dropout — the very thing being measured — so the gap rate comes out inflated by an
        // amount nothing in the report discloses. Refusing costs a re-collection; measuring costs a
        // wrong conclusion nobody can see is wrong.
        var parsed = GazeRecordingFile.Parse(new[]
        {
            "# {\"providerType\":\"Mouse\"}",
            "1,2,1000,1",
            "#! {\"droppedSamples\":740,\"recordedSamples\":1000}",
        });

        Check.That(parsed.Trailer!.Value.DroppedSamples).IsEqualTo(740L);
        Check.That(parsed.UnusableReason).Contains("740");
        Check.ThatCode(() => parsed.EnsureUsable()).Throws<InvalidOperationException>();
    }

    [Fact]
    public void ARecordingWithNoTrailer_IsRefusedAsNeverClosed()
    {
        // Every recording made before the trailer existed looks exactly like one whose process was
        // killed mid-session, and so does a file whose writer faulted. All three are "nobody counted",
        // and none of them may be measured on the assumption that nothing was lost.
        var parsed = GazeRecordingFile.Parse(new[]
        {
            "# {\"providerType\":\"Mouse\"}",
            "1,2,1000,1",
        });

        Check.That(parsed.Trailer).IsNull();
        Check.That(parsed.UnusableReason).Contains("never closed cleanly");
        Check.ThatCode(() => parsed.EnsureUsable()).Throws<InvalidOperationException>();
    }

    [Fact]
    public void ACalibratedRecordingWithoutItsTransform_IsRefused()
    {
        // Claiming a calibration was active while carrying no coefficients means replay cannot reproduce
        // the corrected stream the pipeline saw. Replaying the raw samples anyway would silently measure
        // the tracker's coordinate space and label the result as the user's experience.
        var parsed = GazeRecordingFile.Parse(new[]
        {
            "# {\"providerType\":\"StreamEngine\",\"calibrationActive\":true}",
            "1,2,1000,1",
            "#! {\"droppedSamples\":0,\"recordedSamples\":1}",
        });

        Check.That(parsed.UnusableReason).Contains("could not serialize");
        Check.ThatCode(() => parsed.EnsureUsable()).Throws<InvalidOperationException>();
    }

    [Fact]
    public void ACalibratedRecordingCarryingItsTransform_IsAccepted()
    {
        // The contrast case, so the refusal above is provably about the MISSING transform and not about
        // CalibrationActive being true at all.
        var parsed = GazeRecordingFile.Parse(new[]
        {
            "# {\"providerType\":\"StreamEngine\",\"calibrationActive\":true," +
            "\"calibrationTransform\":{\"a\":1.1,\"b\":0,\"c\":12,\"d\":0,\"e\":0.9,\"f\":-4}}",
            "1,2,1000,1",
            "#! {\"droppedSamples\":0,\"recordedSamples\":1}",
        });

        Check.That(parsed.UnusableReason).IsNull();
        Check.That(parsed.Header.CalibrationTransform!.Value.A).IsEqualTo(1.1);
        Check.That(parsed.Header.CalibrationTransform.Value.F).IsEqualTo(-4.0);
    }

    [Fact]
    public void TheTrailerIsNotMistakenForTheHeader()
    {
        // `#!` starts with `#`, so an order-sensitive parser would eat the trailer as a repeated header
        // and report a recording with no trailer at all — i.e. silently turn every clean recording into
        // a refused one.
        var parsed = GazeRecordingFile.Parse(new[]
        {
            "# {\"providerType\":\"StreamEngine\"}",
            "1,2,1000,1",
            "#! {\"droppedSamples\":0,\"recordedSamples\":1}",
        });

        Check.That(parsed.Header.ProviderType).IsEqualTo("StreamEngine");
        Check.That(parsed.Trailer).IsNotNull();
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
