namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Models;

/// <summary>
/// The passive attempt log. Its reason to exist is the mutation it is tested against: counting
/// completions only. Completions are censored by the hit radius itself, so a bias near that radius
/// makes them vanish and the log reads clean; entries survive, and their ratio is the screen.
/// </summary>
public class PassiveAttemptLogTests
{
    private static readonly PhysicalPoint RegionCenter = new(300, 300);
    private static readonly PhysicalPoint Away = new(1200, 900);
    private const double HitRadius = 120;
    private const int DwellMs = 700;
    private const long StepUs = 30_000;   // ~33 Hz

    // A completing visit ends right after activation, before the detector can treat the still-on-
    // target gaze as a fresh attempt — so one visit is exactly one entry and one completion.
    private const int CompleteVisitsSamples = 26;   // 780 ms > 700 ms dwell
    private const int MissSamples = 10;             // 300 ms, deliberately short

    private static ReplayPipeline.Frame[] Stream(params (PhysicalPoint At, int Samples)[] visits)
    {
        var samples = new List<GazePoint>();
        long t = 0;
        foreach (var (at, count) in visits)
            for (int i = 0; i < count; i++, t += StepUs)
                samples.Add(new GazePoint(at.X, at.Y, t, IsValid: true));

        return ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Unfiltered);
    }

    [Fact]
    public void EntriesAndCompletions_AreBothLoggedPerRegion()
    {
        var frames = Stream(
            (RegionCenter, CompleteVisitsSamples),
            (Away, 10),
            (RegionCenter, MissSamples),
            (Away, 10),
            (RegionCenter, CompleteVisitsSamples),
            (Away, 10));

        var report = PassiveAttemptLog.Analyze(
            frames, [new PassiveAttemptLog.Region("toolbar", RegionCenter, HitRadius, DwellMs)]);

        var region = report.Regions.Single();
        // The missed visit is exactly what a "completions only" log would drop, so both numbers are
        // asserted. Entries == 0 is the named mutation's signature.
        Check.That(region.Entries).IsEqualTo(3);
        Check.That(region.Completions).IsEqualTo(2);
        Check.That(region.CompletionRate).IsCloseTo(2.0 / 3.0, 1e-9);
        Check.That(report.Describe()).Contains("toolbar");
    }

    [Theory]
    [InlineData(2)]   // 20 % — twice the screen, the nearest healthy rate a loose threshold would catch
    [InlineData(4)]   // 40 % — a clearly healthy region
    public void ACompletionRateAboveTenPercent_IsNotFlagged(int completions)
    {
        // The other side of the 10 % screen (holistic review F2): nothing pinned it, so the threshold
        // could drift up to 50 % with every test green and healthy regions would start telling the
        // user to "run the probe" — a stamina-costing false tripwire.
        var visits = new List<(PhysicalPoint, int)>();
        for (int i = 0; i < 10; i++)
        {
            visits.Add((RegionCenter, i < completions ? CompleteVisitsSamples : MissSamples));
            visits.Add((Away, 10));
        }

        var report = PassiveAttemptLog.Analyze(
            Stream(visits.ToArray()), [new PassiveAttemptLog.Region("edge", RegionCenter, HitRadius, DwellMs)]);

        var region = report.Regions.Single();
        Check.That(region.Entries).IsEqualTo(10);
        Check.That(region.Completions).IsEqualTo(completions);
        Check.That(region.FlagsBiasTowardHitRadius).IsFalse();
        Check.That(report.FlaggedRegions).IsEqualTo(0);
    }

    [Fact]
    public void ACompletionRateAtTenPercent_IsFlagged()
    {
        // The debate's screen: a rate falling toward 10 % flags a bias near the hit radius. Ten
        // attempts with one completion is exactly that boundary.
        var visits = new List<(PhysicalPoint, int)> { (RegionCenter, CompleteVisitsSamples) };
        for (int i = 0; i < 9; i++)
        {
            visits.Add((Away, 10));
            visits.Add((RegionCenter, MissSamples));
        }

        var report = PassiveAttemptLog.Analyze(
            Stream(visits.ToArray()), [new PassiveAttemptLog.Region("edge", RegionCenter, HitRadius, DwellMs)]);

        var region = report.Regions.Single();
        Check.That(region.Entries).IsEqualTo(10);
        Check.That(region.Completions).IsEqualTo(1);
        Check.That(region.FlagsBiasTowardHitRadius).IsTrue();
        Check.That(report.FlaggedRegions).IsEqualTo(1);
        Check.That(report.Describe()).Contains("at or below 10");
    }

    [Fact]
    public void ARegionNobodyLookedAt_IsNeitherAFlagNorEvidence()
    {
        // Zero entries must not read as "0 % completion" — a region with no attempts says nothing,
        // and reporting it as evidence of bias would be a false positive with the probe's authority.
        var frames = Stream((Away, 40));

        var report = PassiveAttemptLog.Analyze(
            frames, [new PassiveAttemptLog.Region("quiet", RegionCenter, HitRadius, DwellMs)]);

        Check.That(report.Regions.Single().Entries).IsEqualTo(0);
        Check.That(report.Regions.Single().FlagsBiasTowardHitRadius).IsFalse();
        Check.That(report.FlaggedRegions).IsEqualTo(0);
    }

    [Fact]
    public void Grid_CoversTheScreenWithDistinctCellCentres()
    {
        var regions = PassiveAttemptLog.Grid(new PhysicalPoint(3840, 2400), columns: 3, rows: 2, HitRadius, DwellMs);

        Check.That(regions).HasSize(6);
        Check.That(regions[0].Center.X).IsEqualTo(640.0);     // first cell centre
        Check.That(regions[0].Center.Y).IsEqualTo(600.0);
        Check.That(regions[^1].Center.X).IsEqualTo(3200.0);   // last cell centre
        Check.That(regions.Select(r => r.Name).Distinct().Count()).IsEqualTo(6);
    }
}
