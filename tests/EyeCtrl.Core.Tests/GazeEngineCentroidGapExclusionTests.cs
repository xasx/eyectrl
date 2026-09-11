namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// Gap-held samples must not reach <c>FixationCentroidTracker</c>. The interpolator hands back the frozen
/// last valid position with a real dt, so a blink would otherwise contribute zero-displacement (speed-0)
/// samples — deflating <see cref="GazeEngine.LastFixationJitter"/> exactly when tracking is worst, and
/// pulling <see cref="GazeEngine.StableCentroid"/> toward the freeze.
/// <para>
/// Each test compares a stream containing a blink against a control stream in which those samples are
/// simply absent. After the fix the tracker sees identical input in both, so both statistics must match
/// exactly.
/// </para>
/// </summary>
public class GazeEngineCentroidGapExclusionTests
{
    private const long StepUs = 20_000;   // 50 Hz

    // Small in-radius wander with VARYING per-sample displacement (1 px / 2 px at 20 ms → 50 / 100 px/s).
    // Both stay at or below the 100 px/s velocity gate, so every sample enters the centroid, and the two
    // distinct speeds give the Welford variance something to accumulate.
    private static double WanderX(int i) => 500 + new[] { 0, 1, 3, 4, 2 }[i % 5];

    private static GazePoint Sample(int i) => new(WanderX(i), 500, i * StepUs, IsValid: true);

    [Fact]
    public void ShortGapHeldSamples_DoNotChangeJitter()
    {
        var withGap = Run(BuildStream(includeBlink: true));
        var control = Run(BuildStream(includeBlink: false));

        Check.That(control.Jitter).IsStrictlyGreaterThan(0.0);   // the comparison is not vacuous
        Check.That(withGap.Jitter).IsEqualTo(control.Jitter);
    }

    [Fact]
    public void ShortGapHeldSamples_DoNotChangeCentroid()
    {
        var withGap = Run(BuildStream(includeBlink: true));
        var control = Run(BuildStream(includeBlink: false));

        Check.That(control.Centroid.HasValue).IsTrue();
        Check.That(withGap.Centroid!.Value.X).IsEqualTo(control.Centroid!.Value.X);
        Check.That(withGap.Centroid!.Value.Y).IsEqualTo(control.Centroid!.Value.Y);
    }

    /// <summary>
    /// Valid samples: 20 to establish a fixation (it starts at 300 ms) and carry the centroid past it,
    /// then — optionally — a three-sample blink well inside the 150 ms gap budget, then five more valid
    /// samples. The control omits the blink entirely rather than replacing it, so a correct
    /// implementation feeds the tracker byte-identical input in both runs.
    /// </summary>
    private static List<GazePoint> BuildStream(bool includeBlink)
    {
        var stream = new List<GazePoint>();
        for (int i = 0; i <= 19; i++)
            stream.Add(Sample(i));

        if (includeBlink)
            for (int i = 20; i <= 22; i++)
                stream.Add(new GazePoint(0, 0, i * StepUs, IsValid: false));

        for (int i = 23; i <= 27; i++)
            stream.Add(Sample(i));

        return stream;
    }

    private static (PhysicalPoint? Centroid, double Jitter) Run(List<GazePoint> stream)
    {
        var provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;   // the gap fix, not the filter, is under test

        using var engine = new GazeEngine(provider, new FakeInputSimulator(), settings);
        engine.Start();

        foreach (var point in stream)
            provider.SimulateGaze(point);

        var centroid = engine.StableCentroid;   // read before the fixation ends — StopTracking clears it

        // Leave the fixation radius (50 px default) so FixationEnded latches LastFixationJitter.
        provider.SimulateGaze(new GazePoint(1500, 900, stream[^1].TimestampMicroseconds + StepUs, true));

        return (centroid, engine.LastFixationJitter);
    }
}
