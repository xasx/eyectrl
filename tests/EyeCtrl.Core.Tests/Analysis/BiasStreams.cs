namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// The named adversarial streams the two ticket-6 reviewers built, kept as reusable fixtures so the
/// estimator's behaviour on each of them is a test, not a story. Generator shapes follow reviewer B's
/// saved adversarial tests: a 300 ms lead of far-off samples (the onset guard's diet), then cycles of
/// one hold segment at a per-target bias plus an optional absolute distractor segment.
/// </summary>
internal static class BiasStreams
{
    public static readonly PhysicalPoint Screen = new(3840, 2400);
    public const long StepUs = 30_000;   // ~33 Hz

    internal sealed class Noise(uint seed)
    {
        private uint _state = seed;

        public double NextUnit()
        {
            _state = (1664525u * _state) + 1013904223u;
            return (_state >> 8) / (double)(1 << 24);
        }

        public double NextGaussian()
        {
            double u1 = Math.Max(NextUnit(), double.Epsilon);
            double u2 = NextUnit();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }

    public static IReadOnlyList<PhysicalPoint> Layout => CalibrationTargetPositionHelper.GetPositions(5, Screen);

    public static int CentreIndex()
    {
        var targets = Layout;
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < targets.Count; i++)
        {
            double dx = targets[i].X - (Screen.X / 2), dy = targets[i].Y - (Screen.Y / 2);
            double d = (dx * dx) + (dy * dy);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    public static GroundTruthWindow Window(
        int id, PhysicalPoint target, PhysicalPoint bias, ref long t, Noise noise,
        int cycles = 10, int holdN = 8, PhysicalPoint? distractorAbs = null, int dN = 0, double sigma = 9)
    {
        long from = t;
        var samples = new List<GroundTruthSample>();
        for (int i = 0; i < 10; i++, t += StepUs)   // onset scatter, all inside the 300 ms guard
            samples.Add(new GroundTruthSample(target.X - 400 + (i * 40), target.Y - 300 + (i * 30), t, true));
        for (int c = 0; c < cycles; c++)
        {
            for (int i = 0; i < holdN; i++, t += StepUs)
                samples.Add(new GroundTruthSample(target.X + bias.X + (noise.NextGaussian() * sigma), target.Y + bias.Y + (noise.NextGaussian() * sigma), t, true));
            if (distractorAbs is { } d)
                for (int i = 0; i < dN; i++, t += StepUs)
                    samples.Add(new GroundTruthSample(d.X + (noise.NextGaussian() * sigma), d.Y + (noise.NextGaussian() * sigma), t, true));
        }

        return new GroundTruthWindow(id, target.X, target.Y, from, null, t - StepUs, samples.Count) { Samples = samples };
    }

    public static GroundTruthSession Session(IReadOnlyList<GroundTruthWindow> windows) =>
        new(GroundTruthSession.CurrentSchemaVersion, "StreamEngine", Screen.X, Screen.Y, 300, windows);

    /// <summary>A: per-target bias (65, 62, 10, 10, 10) — the rule unit test's own numbers.</summary>
    public static GroundTruthSession A()
    {
        var targets = Layout;
        var biases = new[]
        {
            new PhysicalPoint(65, 0), new PhysicalPoint(62, 0), new PhysicalPoint(10, 0),
            new PhysicalPoint(10, 0), new PhysicalPoint(10, 0),
        };
        var noise = new Noise(1);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
            windows.Add(Window(i, targets[i], biases[i], ref t, noise));
        return Session(windows);
    }

    /// <summary>B: left column (−40, 0), right column (100, 0), centre (30, 0).</summary>
    public static GroundTruthSession B()
    {
        var targets = Layout;
        var noise = new Noise(2);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            var target = targets[i];
            PhysicalPoint bias;
            int hold;
            if (Math.Abs(target.X - (Screen.X / 2)) < 1) { bias = new PhysicalPoint(30, 0); hold = 8; }
            else if (target.X < Screen.X / 2) { bias = new PhysicalPoint(-40, 0); hold = 12; }
            else { bias = new PhysicalPoint(100, 0); hold = 8; }
            windows.Add(Window(i, target, bias, ref t, noise, holdN: hold));
        }
        return Session(windows);
    }

    /// <summary>C: a 4 % radial scale error — corners ≈ 84 px outward, centre ≈ 0.</summary>
    public static GroundTruthSession C()
    {
        var targets = Layout;
        var noise = new Noise(3);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            var target = targets[i];
            var bias = new PhysicalPoint(0.04 * (target.X - (Screen.X / 2)), 0.04 * (target.Y - (Screen.Y / 2)));
            windows.Add(Window(i, target, bias, ref t, noise));
        }
        return Session(windows);
    }

    /// <summary>D: truth (20, 0) at every target, plus a slightly longer consistent rest at (20, −160).</summary>
    public static GroundTruthSession D()
    {
        var targets = Layout;
        var noise = new Noise(4);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            var target = targets[i];
            windows.Add(Window(i, target, new PhysicalPoint(20, 0), ref t, noise, holdN: 8,
                distractorAbs: new PhysicalPoint(target.X + 20, target.Y - 160), dN: 9));
        }
        return Session(windows);
    }

    /// <summary>F: holds at 30 px on 2 of 5 targets; the other three windows are pure scatter.</summary>
    public static GroundTruthSession F()
    {
        var targets = Layout;
        var noise = new Noise(6);
        long t = 0;
        int centre = CentreIndex();
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            if (i == centre || i == ((centre + 1) % 5))
                windows.Add(Window(i, targets[i], new PhysicalPoint(30, 0), ref t, noise));
            else
                windows.Add(Window(i, targets[i], new PhysicalPoint(0, 0), ref t, noise, cycles: 0));
        }
        return Session(windows);
    }

    /// <summary>
    /// The acceptance stream — b = 300 px, σ 9, a 250 px glance every 400 ms whose cluster is larger
    /// than the hold's — with glance directions either hand-picked per target or random from a seed.
    /// </summary>
    public static GroundTruthSession Acceptance(uint seed, double[]? perTargetDegrees = null)
    {
        var targets = Layout;
        var noise = new Noise(seed);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int id = 0; id < 5; id++)
        {
            var target = targets[id];
            double radians = perTargetDegrees is null
                ? noise.NextUnit() * 2 * Math.PI
                : perTargetDegrees[id] * Math.PI / 180.0;
            var glance = new PhysicalPoint(
                target.X + (250 * Math.Cos(radians)), target.Y + (250 * Math.Sin(radians)));

            long from = t;
            var samples = new List<GroundTruthSample>();
            for (int c = 0; c < 10; c++)
            {
                for (int i = 0; i < 6; i++, t += StepUs)
                    samples.Add(new GroundTruthSample(target.X + 300 + (noise.NextGaussian() * 9), target.Y + (noise.NextGaussian() * 9), t, true));
                for (int i = 0; i < 7; i++, t += StepUs)
                    samples.Add(new GroundTruthSample(glance.X + (noise.NextGaussian() * 9), glance.Y + (noise.NextGaussian() * 9), t, true));
            }
            windows.Add(new GroundTruthWindow(id, target.X, target.Y, from, from, t - StepUs, samples.Count) { Samples = samples });
        }
        return Session(windows);
    }
}
