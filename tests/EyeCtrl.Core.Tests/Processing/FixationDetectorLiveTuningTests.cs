namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Tests for live, state-preserving reconfiguration of <see cref="FixationDetector"/> tolerance
/// (<see cref="FixationDetector.RadiusPixels"/> / <see cref="FixationDetector.FixationTimeMs"/>). These
/// setters exist so <c>GazeEngine</c> can apply an adaptation step without rebuilding the detector, which
/// would drop the in-progress fixation on every controller tick.
/// </summary>
public class FixationDetectorLiveTuningTests
{
    // Feeds n samples 33 ms apart at the same point, starting at ts.
    private static long Hold(FixationDetector d, double x, double y, int samples, long ts)
    {
        for (int i = 0; i < samples; i++)
        {
            ts += 33_000;
            d.Update(new PhysicalPoint(x, y), ts);
        }
        return ts;
    }

    [Fact]
    public void Setters_RoundTripTheirValues()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 300);

        detector.RadiusPixels = 75.5;
        detector.FixationTimeMs = 180;

        Check.That(detector.RadiusPixels).IsEqualTo(75.5);
        Check.That(detector.FixationTimeMs).IsEqualTo(180);
    }

    [Fact]
    public void ConstructorValues_AreVisibleThroughTheProperties()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 42, fixationTimeMs: 250);

        Check.That(detector.RadiusPixels).IsEqualTo(42.0);
        Check.That(detector.FixationTimeMs).IsEqualTo(250);
    }

    [Fact]
    public void WideningRadius_PreservesAnActiveFixation()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 100);
        long ts = Hold(detector, 500, 500, samples: 6, ts: 0);
        Check.That(detector.IsFixating).IsTrue();

        detector.RadiusPixels = 90;

        ts += 33_000;
        detector.Update(new PhysicalPoint(500, 500), ts);
        Check.That(detector.IsFixating).IsTrue();
    }

    [Fact]
    public void ShrinkingRadius_EndsFixationOnlyWhenGazeFallsOutside()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 80, fixationTimeMs: 100);
        bool ended = false;
        detector.FixationEnded += () => ended = true;

        // Center is the first sample (500, 500); subsequent samples sit 40 px away, inside radius 80.
        long ts = 33_000;
        detector.Update(new PhysicalPoint(500, 500), ts);
        ts = Hold(detector, 540, 500, samples: 6, ts: ts);
        Check.That(detector.IsFixating).IsTrue();

        detector.RadiusPixels = 60; // still wider than the 40 px offset → nothing happens
        ts += 33_000;
        detector.Update(new PhysicalPoint(540, 500), ts);
        Check.That(detector.IsFixating).IsTrue();
        Check.That(ended).IsFalse();

        detector.RadiusPixels = 20; // now narrower than the offset → ordinary gaze-left
        ts += 33_000;
        detector.Update(new PhysicalPoint(540, 500), ts);
        Check.That(detector.IsFixating).IsFalse();
        Check.That(ended).IsTrue();
    }

    [Fact]
    public void ShorteningFixationTime_FixatesOnTheAlreadyElapsedHold()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 1000);
        long ts = Hold(detector, 500, 500, samples: 6, ts: 0); // ~198 ms held, 1000 ms required
        Check.That(detector.IsFixating).IsFalse();

        detector.FixationTimeMs = 100;

        ts += 33_000;
        detector.Update(new PhysicalPoint(500, 500), ts);
        Check.That(detector.IsFixating).IsTrue();
    }

    [Fact]
    public void LengtheningFixationTime_LeavesAnEstablishedFixationIntact()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 100);
        long ts = Hold(detector, 500, 500, samples: 6, ts: 0);
        Check.That(detector.IsFixating).IsTrue();

        detector.FixationTimeMs = 900; // only a gaze-left ends a fixation, never a longer requirement

        ts += 33_000;
        detector.Update(new PhysicalPoint(500, 500), ts);
        Check.That(detector.IsFixating).IsTrue();
    }

    [Fact]
    public void FixationTime_RescalesProgress()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 400);
        Hold(detector, 500, 500, samples: 4, ts: 0); // center at sample 1, ~99 ms elapsed after it

        double before = detector.GetProgress();
        detector.FixationTimeMs = 200; // same elapsed time against half the requirement → double progress
        double after = detector.GetProgress();

        Check.That(after).IsStrictlyGreaterThan(before);
        Check.That(after).IsEqualTo(Math.Min(1.0, before * 2));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void RadiusSetter_RejectsNonPositive(double radius)
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 300);

        ArgumentOutOfRangeException? exception = null;
        try { detector.RadiusPixels = radius; }
        catch (ArgumentOutOfRangeException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(detector.RadiusPixels).IsEqualTo(50.0); // rejected value is not applied
    }

    [Fact]
    public void RadiusSetter_RejectsNonFinite()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 300);

        ArgumentException? exception = null;
        try { detector.RadiusPixels = double.NaN; }
        catch (ArgumentException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(detector.RadiusPixels).IsEqualTo(50.0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void FixationTimeSetter_RejectsNonPositive(int timeMs)
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 300);

        ArgumentOutOfRangeException? exception = null;
        try { detector.FixationTimeMs = timeMs; }
        catch (ArgumentOutOfRangeException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(detector.FixationTimeMs).IsEqualTo(300);
    }
}
