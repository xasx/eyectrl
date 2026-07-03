namespace EyeCtrl.Benchmarks;

using EyeCtrl.Core.Models;

/// <summary>
/// Deterministic, procedural generator of synthetic gaze streams for benchmarking.
/// All streams are <see cref="SamplesPerBatch"/> points long at <see cref="SamplePeriodMicros"/>
/// (30 Hz) so cross-scenario ns/op is directly comparable. Use a fixed <paramref name="seed"/>
/// for reproducibility across machines and runs.
/// </summary>
/// <remarks>
/// <see cref="GazePoint"/> is a readonly record struct, so streams are flat value arrays
/// allocated once in GlobalSetup (untimed). The generator writes pixel-scale values into GazePoint.X/Y (the tracker's
/// documented native space; calibration mapping is excluded as a non-goal — see the design spec).
/// </remarks>
public static class GazeStreamGenerator
{
    /// <summary>Number of gaze samples per generated stream.</summary>
    public const int SamplesPerBatch = 1000;
    /// <summary>Microseconds between samples (≈30 Hz, Tobii's common rate).</summary>
    public const long SamplePeriodMicros = 33_333;
    /// <summary>Pinned seed for reproducible generation.</summary>
    public const int DefaultSeed = 42;

    private const double ScreenCenterX = 960.0;
    private const double ScreenCenterY = 540.0;

    /// <summary>
    /// Fixation: gaze held near (960, 540) with slow circular drift (radius ~8 px, ~0.3 Hz)
    /// and ±1.5 px Gaussian noise. 100% valid. Exercises steady-state smoothing and OneEuro's
    /// low-velocity branch.
    /// </summary>
    public static GazePoint[] GenerateFixation(int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        var points = new GazePoint[SamplesPerBatch];
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            long t = i * SamplePeriodMicros;
            double phase = 2 * Math.PI * 0.3 * (t / 1_000_000.0); // ~0.3 Hz over elapsed seconds
            double dx = Math.Cos(phase) * 8.0 + Gaussian(rng) * 1.5;
            double dy = Math.Sin(phase) * 8.0 + Gaussian(rng) * 1.5;
            points[i] = new GazePoint(ScreenCenterX + dx, ScreenCenterY + dy, t, IsValid: true);
        }
        return points;
    }

    /// <summary>
    /// Saccade: 5 point-to-point jumps across the screen, each held briefly, with ±0.5 px noise.
    /// 100% valid. Exercises the high-velocity filter branch and FixationDetector reset churn.
    /// </summary>
    public static GazePoint[] GenerateSaccade(int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        var points = new GazePoint[SamplesPerBatch];
        // 5 targets, evenly dividing the 1000-sample batch into 5 segments of 200.
        double[][] targets =
        {
            new[] { 960.0, 540.0 },
            new[] { 200.0, 200.0 },
            new[] { 1500.0, 300.0 },
            new[] { 400.0, 800.0 },
            new[] { 1200.0, 700.0 },
        };
        int segmentLen = SamplesPerBatch / targets.Length;
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            long t = i * SamplePeriodMicros;
            int seg = Math.Min(i / segmentLen, targets.Length - 1);
            double tx = targets[seg][0] + Gaussian(rng) * 0.5;
            double ty = targets[seg][1] + Gaussian(rng) * 0.5;
            points[i] = new GazePoint(tx, ty, t, IsValid: true);
        }
        return points;
    }

    /// <summary>
    /// NoisyFixation: held at (960, 540), near-stationary, with heavy ±15 px Gaussian jitter.
    /// 100% valid. The aggressive jitter case filters exist to tame.
    /// </summary>
    public static GazePoint[] GenerateNoisyFixation(int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        var points = new GazePoint[SamplesPerBatch];
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            long t = i * SamplePeriodMicros;
            double dx = Gaussian(rng) * 15.0;
            double dy = Gaussian(rng) * 15.0;
            points[i] = new GazePoint(ScreenCenterX + dx, ScreenCenterY + dy, t, IsValid: true);
        }
        return points;
    }

    /// <summary>
    /// WithDropout: the Fixation drift trajectory with ~8% of samples marked invalid, clustered
    /// into two gaps. The short gap (2 invalid samples, ~66 ms) is under the 150 ms
    /// MaxGapInterpolationMs threshold and exercises GapInterpolator's hold-position path; the
    /// long gap (6 invalid samples, ~200 ms) is over the threshold and exercises the reset path.
    /// Placed so valid samples surround them.
    /// </summary>
    public static GazePoint[] GenerateWithDropout(int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        var points = new GazePoint[SamplesPerBatch];
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            long t = i * SamplePeriodMicros;
            double phase = 2 * Math.PI * 0.3 * (t / 1_000_000.0);
            double dx = Math.Cos(phase) * 8.0 + Gaussian(rng) * 1.5;
            double dy = Math.Sin(phase) * 8.0 + Gaussian(rng) * 1.5;
            points[i] = new GazePoint(ScreenCenterX + dx, ScreenCenterY + dy, t, IsValid: true);
        }

        // Short gap (2 samples) near the start → under threshold → held.
        points[50] = points[50] with { IsValid = false };
        points[51] = points[51] with { IsValid = false };

        // Long gap (6 samples) near the middle → over threshold → reset.
        for (int i = 500; i < 506; i++)
            points[i] = points[i] with { IsValid = false };

        return points;
    }

    /// <summary>Standard-normal sample via Box–Muller transform (no extra dependency).</summary>
    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
