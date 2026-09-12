namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// The ticket's acceptance criterion, end to end: a scripted run's recording plus its ground-truth
/// sidecar score through <c>MeasureFixationDeviation(segment, groundTruth)</c> to ABSOLUTE accuracy.
/// <para>
/// Absolute is the whole point. A free-view recording can only be scored against its own centroid,
/// which by construction cannot see a systematic offset: if every sample sits 40 px left of where the
/// user was looking, the centroid sits 40 px left too and the deviation reads as zero. These tests
/// build exactly that situation and check the scripted path catches what the self-referential one
/// cannot.
/// </para>
/// </summary>
public class ScriptedGroundTruthScoringTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eyectrl-gtscore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private const long StepUs = 33_333;

    /// <summary>
    /// Simulates a scripted run: for each target, some searching samples then a held fixation offset
    /// from the target by a known amount. Returns the raw stream and the sidecar it produced, built
    /// through the REAL recorder so the two are aligned the way a live session aligns them.
    /// </summary>
    private (GazePoint[] Samples, GroundTruthSession Session) SimulateRun(
        (PhysicalPoint Target, double OffsetX, double OffsetY)[] targets,
        int searchSamples = 4, int holdSamples = 30)
    {
        var sidecarPath = Path.Combine(_dir, "gt.json");
        var recorder = new ScriptedGroundTruthRecorder(sidecarPath);
        recorder.BeginSession("Mouse", new PhysicalPoint(1920, 1080), settleWindowMs: 300);

        var samples = new List<GazePoint>();
        long t = 0;

        for (int id = 0; id < targets.Length; id++)
        {
            var (target, offsetX, offsetY) = targets[id];

            // Searching: gaze elsewhere, not yet arrived.
            for (int i = 0; i < searchSamples; i++, t += StepUs)
            {
                samples.Add(new GazePoint(target.X - 300, target.Y - 300, t, IsValid: true));
                recorder.OnSample(id, target, t, hasArrived: false);
            }

            // Holding: gaze parked at a fixed offset from the target, with a tiny alternation so the
            // segment is not a single repeated point.
            for (int i = 0; i < holdSamples; i++, t += StepUs)
            {
                double wobble = (i % 2 == 0) ? 1.0 : -1.0;
                samples.Add(new GazePoint(target.X + offsetX + wobble, target.Y + offsetY, t, IsValid: true));
                recorder.OnSample(id, target, t, hasArrived: true);
            }
        }

        recorder.CompleteSession();
        return (samples.ToArray(), GroundTruthFile.Read(sidecarPath));
    }

    [Fact]
    public void AScriptedRunScoresAbsoluteDeviationPerTarget()
    {
        // Each target is held 40 px to the right. Absolute deviation must recover ~40 px; the
        // self-referential measurement over the same samples sees ~1 px, because the centroid moved
        // with the offset. That gap IS the reason scripted collection exists.
        var (samples, session) = SimulateRun(new[]
        {
            (new PhysicalPoint(384, 216), 40.0, 0.0),
            (new PhysicalPoint(960, 540), 40.0, 0.0),
            (new PhysicalPoint(1536, 864), 40.0, 0.0),
        });

        var frames = ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Unfiltered);
        var scored = GroundTruthFile.Score(frames, session);

        Check.That(scored).HasSize(3);
        foreach (var target in scored)
        {
            Check.That(target.Deviation.RmsPixels).IsStrictlyGreaterThan(39.0);
            Check.That(target.Deviation.RmsPixels).IsStrictlyLessThan(42.0);
        }

        var selfReferential = GazeMetrics.MeasureFixationDeviation(
            GroundTruthFile.Slice(frames, session.Windows[0]));
        Check.That(selfReferential.RmsPixels).IsStrictlyLessThan(2.0);
    }

    [Fact]
    public void ScoringExcludesTheSearchBeforeArrival()
    {
        // The searching samples sit 300 px away. If the window started at first display instead of at
        // arrival, they would be scored against the target and the deviation would explode.
        var (samples, session) = SimulateRun(new[] { (new PhysicalPoint(960, 540), 5.0, 0.0) });

        var frames = ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Unfiltered);
        var scored = GroundTruthFile.Score(frames, session);

        Check.That(scored).HasSize(1);
        Check.That(scored[0].Deviation.RmsPixels).IsStrictlyLessThan(10.0);
        Check.That(scored[0].Deviation.SampleCount).IsEqualTo(30);   // the held samples only
    }

    [Fact]
    public void SliceSelectsOnlyTheFramesInsideAWindow()
    {
        var (samples, session) = SimulateRun(new[]
        {
            (new PhysicalPoint(100, 100), 0.0, 0.0),
            (new PhysicalPoint(900, 900), 0.0, 0.0),
        });
        var frames = ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Unfiltered);

        var first = GroundTruthFile.Slice(frames, session.Windows[0]);
        var second = GroundTruthFile.Slice(frames, session.Windows[1]);

        Check.That(first).HasSize(30);
        Check.That(second).HasSize(30);
        // Disjoint in time: no frame may be scored against two different targets.
        Check.That(first[^1].Raw.TimestampMicroseconds)
             .IsStrictlyLessThan(second[0].Raw.TimestampMicroseconds);
    }

    [Fact]
    public void AWindowThatCaughtNoFramesIsSkipped_NotScoredAsPerfect()
    {
        // "No data" and "perfect" must never look alike in a report that drives a roadmap decision.
        var (samples, session) = SimulateRun(new[] { (new PhysicalPoint(500, 500), 0.0, 0.0) });
        var frames = ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Unfiltered);

        var orphan = new GroundTruthSession(
            GroundTruthSession.CurrentSchemaVersion, "Mouse", 1920, 1080, 300,
            new[] { new GroundTruthWindow(99, 10, 10, 90_000_000, 90_000_000, 91_000_000, 0) });

        Check.That(GroundTruthFile.Score(frames, orphan)).IsEmpty();
        Check.That(GroundTruthFile.Score(frames, session)).HasSize(1);
    }

    [Fact]
    public void AWindowWhoseFramesAreAllGapHeld_IsSkipped_NotScoredAsPerfect()
    {
        // The trap the invalid-sample change opens if left unpaired. A target shown entirely during
        // tracking loss now DOES produce a window (that is the point — silently absent windows are
        // indistinguishable from a protocol slip), but every frame in it is gap-held.
        // MeasureFixationDeviation excludes held samples, so scoring such a window would return
        // (0, 0, 0) — reporting PERFECT absolute accuracy on no data at all.
        // NB the leading valid sample is load-bearing, and getting it wrong is an easy mistake: by the
        // Stage-1 gap invariant, invalid samples before ANY valid one are NOT in-gap — there is no
        // stream yet to be in a gap of. A window of pure dropout from t=0 would therefore replay as
        // not-held and score, which is not the case under test.
        var samples = new List<GazePoint> { new(960, 540, 0, IsValid: true) };
        long windowStart = StepUs;
        long t = windowStart;
        for (int i = 0; i < 20; i++, t += StepUs)
            samples.Add(new GazePoint(0, 0, t, IsValid: false));

        var frames = ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Unfiltered);
        var session = new GroundTruthSession(
            GroundTruthSession.CurrentSchemaVersion, "Mouse", 1920, 1080, 300,
            new[] { new GroundTruthWindow(0, 960, 540, windowStart, windowStart, t, 20) });

        Check.That(GroundTruthFile.Slice(frames, session.Windows[0]).All(f => f.IsInGap)).IsTrue();
        Check.That(GroundTruthFile.Score(frames, session)).IsEmpty();
    }

    [Fact]
    public void AWindowWithSomeValidFrames_IsStillScored()
    {
        // The other half of the guard: a partially blinked target is real evidence and must survive.
        // Same leading-valid-sample requirement as above, so the dropout really replays as held.
        var samples = new List<GazePoint> { new(965, 540, 0, IsValid: true) };
        long windowStart = StepUs;
        long t = windowStart;
        for (int i = 0; i < 10; i++, t += StepUs)
            samples.Add(new GazePoint(0, 0, t, IsValid: false));      // blinked through
        for (int i = 0; i < 20; i++, t += StepUs)
            samples.Add(new GazePoint(965, 540, t, IsValid: true));   // then held, 5 px off target

        var frames = ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Unfiltered);
        var session = new GroundTruthSession(
            GroundTruthSession.CurrentSchemaVersion, "Mouse", 1920, 1080, 300,
            new[] { new GroundTruthWindow(0, 960, 540, windowStart, windowStart, t, 30) });

        var scored = GroundTruthFile.Score(frames, session);
        Check.That(scored).HasSize(1);
        Check.That(scored[0].Deviation.SampleCount).IsEqualTo(20);   // the held samples are excluded
        Check.That(scored[0].Deviation.RmsPixels).IsCloseTo(5.0, 0.5);
    }

    // ── Stale-sidecar detection ──

    [Fact]
    public void Alignment_ReportsAlignedWhenTheSidecarMatchesTheRecording()
    {
        var (samples, session) = SimulateRun(new[] { (new PhysicalPoint(500, 500), 0.0, 0.0) });

        var alignment = GroundTruthFile.CheckAlignment(session, samples);

        Check.That(alignment.IsStale).IsFalse();
        Check.That(alignment.WindowsOutsideRecording).IsEqualTo(0);
        Check.That(alignment.Describe()).Contains("aligned");
    }

    [Fact]
    public void Alignment_DetectsAStaleSidecarFromAnEarlierRun()
    {
        // The Low-4 hazard: a cancelled run writes no sidecar, so the PREVIOUS run's sidecar sits
        // beside a recording that has since grown. The window count still looks right, so nothing
        // about the file itself gives the staleness away — only the device timestamps do.
        var (_, oldSession) = SimulateRun(new[] { (new PhysicalPoint(500, 500), 0.0, 0.0) });

        // A later recording, well past the old session's span.
        var newSamples = new List<GazePoint>();
        for (int i = 0; i < 50; i++)
            newSamples.Add(new GazePoint(500, 500, 900_000_000 + i * StepUs, IsValid: true));

        var alignment = GroundTruthFile.CheckAlignment(oldSession, newSamples);

        Check.That(alignment.IsStale).IsTrue();
        Check.That(alignment.WindowsInsideRecording).IsEqualTo(0);
        Check.That(alignment.Describe()).Contains("STALE");
        Check.That(alignment.Describe()).Contains("Re-collect");
    }

    [Fact]
    public void Alignment_ReportsPartialOverlapRatherThanAllOrNothing()
    {
        // A recording that began late keeps its later windows; the verdict says how many survived
        // instead of throwing the whole session away.
        var (samples, session) = SimulateRun(new[]
        {
            (new PhysicalPoint(100, 100), 0.0, 0.0),
            (new PhysicalPoint(900, 900), 0.0, 0.0),
        });

        // Drop the first half of the recording, so only the second window still overlaps.
        var late = samples.Where(s => s.TimestampMicroseconds > session.Windows[0].EndedAtUs).ToList();

        var alignment = GroundTruthFile.CheckAlignment(session, late);

        Check.That(alignment.IsStale).IsFalse();
        Check.That(alignment.WindowsInsideRecording).IsEqualTo(1);
        Check.That(alignment.WindowsOutsideRecording).IsEqualTo(1);
        Check.That(alignment.Describe()).Contains("PARTIAL");
    }

    [Fact]
    public void Alignment_OnAnEmptyRecording_IsStale()
    {
        var (_, session) = SimulateRun(new[] { (new PhysicalPoint(500, 500), 0.0, 0.0) });

        Check.That(GroundTruthFile.CheckAlignment(session, Array.Empty<GazePoint>()).IsStale).IsTrue();
    }

    // ── Per-target sigma from the calibration the same run produces ──

    private static CalibrationData CalibrationWith(params (double X, double Y, double SigmaX, double SigmaY)[] points) =>
        new()
        {
            ScreenWidth = 1920,
            ScreenHeight = 1080,
            Points = points.Select(p => new CalibrationPoint(
                new PhysicalPoint(p.X, p.Y), new PhysicalPoint(p.X, p.Y), p.SigmaX, p.SigmaY, 30)).ToArray(),
        };

    [Fact]
    public void SigmaReport_ClassifiesCenterCornerAndEdge()
    {
        // The layout puts targets at 20 % / 50 % / 80 % of each axis.
        var report = CalibrationSigmaReport.Build(CalibrationWith(
            (384, 216, 1, 1),     // 0.2 / 0.2 → corner
            (960, 540, 1, 1),     // 0.5 / 0.5 → center
            (960, 216, 1, 1)));   // 0.5 / 0.2 → edge

        Check.That(report.Targets[0].Zone).IsEqualTo(CalibrationSigmaReport.Zone.Corner);
        Check.That(report.Targets[1].Zone).IsEqualTo(CalibrationSigmaReport.Zone.Center);
        Check.That(report.Targets[2].Zone).IsEqualTo(CalibrationSigmaReport.Zone.Edge);
    }

    [Fact]
    public void SigmaReport_SurfacesTheCornerToCenterRatio()
    {
        // The split that separates DEVICE noise from MAPPING error: if the corners are far noisier
        // than the centre, the problem is geometry, not a noise floor a filter could lower.
        var report = CalibrationSigmaReport.Build(CalibrationWith(
            (960, 540, 3, 4),      // center: radial 5
            (384, 216, 9, 12),     // corner: radial 15
            (1536, 864, 9, 12)));  // corner: radial 15

        Check.That(report.CenterSigmaRadial).IsEqualTo(5.0);
        Check.That(report.CornerSigmaRadial).IsEqualTo(15.0);
        Check.That(report.CornerToCenterRatio).IsEqualTo(3.0);
        Check.That(report.Describe()).Contains("suspect mapping/geometry");
    }

    [Fact]
    public void SigmaReport_ReportsUniformNoiseWhenTheRatioIsLow()
    {
        var report = CalibrationSigmaReport.Build(CalibrationWith(
            (960, 540, 3, 4),
            (384, 216, 3, 4)));

        Check.That(report.CornerToCenterRatio).IsEqualTo(1.0);
        Check.That(report.Describe()).Contains("broadly uniform");
    }

    [Fact]
    public void SigmaReport_RmsAndMedianDifferOnASkewedSet()
    {
        // One bad target must move the RMS more than the median — that is why both are reported
        // rather than a single average that a single outlier could dominate.
        var report = CalibrationSigmaReport.Build(CalibrationWith(
            (960, 540, 3, 4),      // radial 5
            (384, 216, 3, 4),      // radial 5
            (1536, 864, 30, 40))); // radial 50

        Check.That(report.MedianSigmaRadial).IsEqualTo(5.0);
        Check.That(report.RmsSigmaRadial).IsStrictlyGreaterThan(20.0);
    }

    [Fact]
    public void SigmaReport_OnAnEmptyCalibration_ClaimsNothing()
    {
        var report = CalibrationSigmaReport.Build(new CalibrationData());

        Check.That(report.Targets).IsEmpty();
        Check.That(report.CornerToCenterRatio).IsNull();
        Check.That(report.Describe()).Contains("no targets");
    }
}
