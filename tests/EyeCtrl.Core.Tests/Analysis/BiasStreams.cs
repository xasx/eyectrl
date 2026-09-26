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

    /// <summary>
    /// Reviewer B's sweep stream: a hold at target + <paramref name="bias"/>, then a random-direction
    /// glance at <paramref name="glanceR"/> px whose run is one sample longer than the hold's — the
    /// ticket's acceptance shape at any bias, so the small-bias side can be swept too. A radius range
    /// draws each target's distance from it (the incidental-second-place shape); the default range
    /// consumes no random draw, so the fixed-radius sweeps are unchanged.
    /// </summary>
    public static GroundTruthSession GlanceStream(
        uint seed, PhysicalPoint bias, int cycles, int holdN = 6, int glanceN = 7, double glanceR = 250,
        double? glanceRMin = null, double? glanceRMax = null)
    {
        var targets = Layout;
        var noise = new Noise(seed);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int id = 0; id < 5; id++)
        {
            var target = targets[id];
            double radians = noise.NextUnit() * 2 * Math.PI;
            double radius = glanceR;
            if (glanceRMin is { } min && glanceRMax is { } max && max > min)
                radius = min + (noise.NextUnit() * (max - min));
            var glance = new PhysicalPoint(
                target.X + (radius * Math.Cos(radians)), target.Y + (radius * Math.Sin(radians)));
            windows.Add(Window(id, target, bias, ref t, noise, cycles, holdN, glance, glanceN));
        }
        return Session(windows);
    }

    /// <summary>
    /// A rest at a FIXED screen spot (reviewer A's G9: the instruction text at the top centre) that is
    /// longer than the hold — a screen position, which is an exact affine field with gradient −1.
    /// </summary>
    public static GroundTruthSession FixedSpotRest(
        uint seed, PhysicalPoint bias, PhysicalPoint spot, int holdN, int readN, int cycles = 10)
    {
        var targets = Layout;
        var noise = new Noise(seed);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int id = 0; id < 5; id++)
            windows.Add(Window(id, targets[id], bias, ref t, noise, cycles, holdN, spot, readN));
        return Session(windows);
    }

    /// <summary>Verdict and recovery counts over a 200-seed sweep.</summary>
    /// <param name="Recalibrate">Seeds whose verdict was Recalibrate.</param>
    /// <param name="NoTrigger">Seeds whose verdict was No trigger.</param>
    /// <param name="Inconclusive">Seeds whose verdict was Inconclusive.</param>
    /// <param name="Ambiguous">Seeds whose estimate was flagged Ambiguous.</param>
    /// <param name="Within20">Seeds whose mean bias landed within 20 px of the truth on both axes.</param>
    public readonly record struct SweepResult(
        int Recalibrate, int NoTrigger, int Inconclusive, int Ambiguous, int Within20)
    {
        /// <summary>Seeds counted.</summary>
        public int Total => Recalibrate + NoTrigger + Inconclusive;
    }

    public static SweepResult Sweep(Func<uint, GroundTruthSession> make, PhysicalPoint truth, int seeds = 200)
    {
        int recalibrate = 0, noTrigger = 0, inconclusive = 0, ambiguous = 0, within20 = 0;
        for (uint seed = 1; seed <= seeds; seed++)
        {
            var estimate = BiasEstimator.Measure(make(seed));
            var decision = BiasDecisionRule.Evaluate(estimate, Screen);
            switch (decision.Verdict)
            {
                case BiasDecisionRule.BiasVerdict.Recalibrate: recalibrate++; break;
                case BiasDecisionRule.BiasVerdict.NoTrigger: noTrigger++; break;
                default: inconclusive++; break;
            }
            if (estimate.Ambiguous) ambiguous++;
            if (estimate.Bias is { } bias
                && Math.Abs(bias.X - truth.X) <= 20 && Math.Abs(bias.Y - truth.Y) <= 20)
                within20++;
        }
        return new SweepResult(recalibrate, noTrigger, inconclusive, ambiguous, within20);
    }

    /// <summary>
    /// Reviewer A's with-wander window: a main hold at <paramref name="bias"/>, plus ONE brief wander
    /// segment (in the middle cycle) at <paramref name="wanderOffset"/> from the target.
    /// </summary>
    public static GroundTruthWindow WithWander(
        int id, PhysicalPoint target, PhysicalPoint bias, PhysicalPoint? wanderOffset, int wanderN,
        ref long t, Noise noise, int cycles = 10, int holdN = 8)
    {
        long from = t;
        var samples = new List<GroundTruthSample>();
        for (int i = 0; i < 10; i++, t += StepUs)
            samples.Add(new GroundTruthSample(target.X - 400 + (i * 40), target.Y - 300 + (i * 30), t, true));
        for (int c = 0; c < cycles; c++)
        {
            for (int i = 0; i < holdN; i++, t += StepUs)
                samples.Add(new GroundTruthSample(
                    target.X + bias.X + (noise.NextGaussian() * 9), target.Y + bias.Y + (noise.NextGaussian() * 9), t, true));
            if (wanderOffset is { } wander && c == cycles / 2)
                for (int i = 0; i < wanderN; i++, t += StepUs)
                    samples.Add(new GroundTruthSample(
                        target.X + wander.X + (noise.NextGaussian() * 9), target.Y + wander.Y + (noise.NextGaussian() * 9), t, true));
        }
        return new GroundTruthWindow(id, target.X, target.Y, from, null, t - StepUs, samples.Count) { Samples = samples };
    }

    /// <summary>
    /// Reviewer A's G13 sweep shape: a non-affine truth (centre-only 90 px, or two diagonal corners at
    /// 110 px) with ONE random 180 ms wander 50–120 px from the deviant target's hold.
    /// </summary>
    public static GroundTruthSession G13(uint seed, string kind, int wanderN)
    {
        var targets = Layout;
        int centre = CentreIndex();
        var noise = new Noise(seed * 7919);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            bool deviant = kind == "centre90" ? i == centre : (i == 0 || i == 3);
            var main = deviant
                ? new PhysicalPoint(kind == "centre90" ? 90 : 110, 0)
                : new PhysicalPoint(10, 0);
            PhysicalPoint? wander = null;
            if (deviant)
            {
                double angle = noise.NextUnit() * 2 * Math.PI;
                double radius = 50 + (70 * noise.NextUnit());
                wander = new PhysicalPoint(
                    main.X + (radius * Math.Cos(angle)), main.Y + (radius * Math.Sin(angle)));
            }
            windows.Add(WithWander(i, targets[i], main, wander, wanderN, ref t, noise));
        }
        return Session(windows);
    }

    /// <summary>
    /// Reviewer A's G12: TL and TR truly at <paramref name="trueX"/> with a second place
    /// <paramref name="gap"/> px closer to the target at equal samples (0 = no second place).
    /// </summary>
    public static GroundTruthSession TwoPlaceField(double trueX, double gap)
    {
        var targets = Layout;
        var noise = new Noise(121);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            double yn = (targets[i].Y - (Screen.Y / 2)) / 1080.0;
            var bias = new PhysicalPoint(((trueX + 10) / 2) - (((trueX - 10) / 2) * yn), 0);
            bool top = i == 0 || i == 4;
            windows.Add(top && gap > 0
                ? Window(i, targets[i], bias, ref t, noise, holdN: 8,
                    distractorAbs: new PhysicalPoint(targets[i].X + bias.X - gap, targets[i].Y), dN: 8)
                : Window(i, targets[i], bias, ref t, noise));
        }
        return Session(windows);
    }

    /// <summary>
    /// Reviewer B's stream K: truth at <paramref name="bias"/> with a consistent rest
    /// <paramref name="restOffset"/> px from the target at every target, the rest holding
    /// <paramref name="restSamples"/> vs the truth's <paramref name="holdSamples"/> per cycle.
    /// </summary>
    public static GroundTruthSession TwoPlace(
        PhysicalPoint bias, PhysicalPoint restOffset, int holdSamples, int restSamples,
        int cycles = 6, uint seed = 500)
    {
        var targets = Layout;
        var noise = new Noise(seed);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            var target = targets[i];
            windows.Add(Window(i, target, bias, ref t, noise, cycles, holdN: holdSamples,
                distractorAbs: new PhysicalPoint(target.X + restOffset.X, target.Y + restOffset.Y), dN: restSamples));
        }
        return Session(windows);
    }

    /// <summary>
    /// Reviewer B's near-rest shape: truth (20, 0) at every target with a random-direction second rest
    /// <paramref name="radiusMin"/>–<paramref name="radiusMax"/> px from the HOLD.
    /// </summary>
    public static GroundTruthSession NearRest(
        uint seed, int cycles, int holdSamples, int restSamples, double radiusMin, double radiusMax,
        PhysicalPoint? bias = null)
    {
        var targets = Layout;
        var truth = bias ?? new PhysicalPoint(20, 0);
        var noise = new Noise(seed + 2000);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int id = 0; id < 5; id++)
        {
            var target = targets[id];
            double radians = noise.NextUnit() * 2 * Math.PI;
            double radius = radiusMin + (noise.NextUnit() * (radiusMax - radiusMin));
            var rest = new PhysicalPoint(
                target.X + truth.X + (radius * Math.Cos(radians)),
                target.Y + truth.Y + (radius * Math.Sin(radians)));
            windows.Add(Window(id, target, truth, ref t, noise, cycles, holdN: holdSamples,
                distractorAbs: rest, dN: restSamples));
        }
        return Session(windows);
    }

    /// <summary>
    /// Reviewer A's jitter stream: a clean bias at every target, no distractor, with the given
    /// per-sample noise σ. The 28 px linkage fragments a fixation at σ above the measured 8.9 px.
    /// </summary>
    public static GroundTruthSession Jitter(uint seed, double sigma, PhysicalPoint? bias = null)
    {
        var targets = Layout;
        var truth = bias ?? new PhysicalPoint(20, 0);
        var noise = new Noise(seed * 7);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
            windows.Add(Window(i, targets[i], truth, ref t, noise, sigma: sigma));
        return Session(windows);
    }

    /// <summary>
    /// Reviewer B's R4-1 shape: a modestly non-affine true field (top row 70/62 px, bottom row 10,
    /// centre <paramref name="centreX"/>) with a second rest 30 px from the target at both top corners,
    /// slightly longer than the hold.
    /// </summary>
    public static GroundTruthSession TwoCornerSlack(
        uint seed, double centreX, int holdSamples, int restSamples, int cycles = 7)
    {
        var targets = Layout;
        var noise = new Noise(seed + 7300);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int id = 0; id < 5; id++)
        {
            var target = targets[id];
            var bias = target.Y < (Screen.Y / 2)
                ? new PhysicalPoint(target.X < (Screen.X / 2) ? 70 : 62, 0)
                : target.Y > (Screen.Y / 2)
                    ? new PhysicalPoint(10, 0)
                    : new PhysicalPoint(centreX, 0);
            bool top = target.Y < (Screen.Y / 2);
            windows.Add(Window(id, target, bias, ref t, noise, cycles, holdN: top ? holdSamples : 7,
                distractorAbs: top ? new PhysicalPoint(target.X + 30, target.Y) : null,
                dN: top ? restSamples : 0));
        }
        return Session(windows);
    }

    /// <summary>
    /// Stream K's two places with the REST enumerated first in time (the tie-break's other order):
    /// the rest runs before the hold in every cycle, so an implementation that dropped the
    /// sample-count tie-break would pick the rest first.
    /// </summary>
    public static GroundTruthSession TwoPlaceRestFirst(
        PhysicalPoint bias, PhysicalPoint restOffset, int holdSamples, int restSamples,
        int cycles = 6, uint seed = 500)
    {
        var targets = Layout;
        var noise = new Noise(seed);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int id = 0; id < 5; id++)
        {
            var target = targets[id];
            long from = t;
            var samples = new List<GroundTruthSample>();
            for (int i = 0; i < 10; i++, t += StepUs)
                samples.Add(new GroundTruthSample(target.X - 400 + (i * 40), target.Y - 300 + (i * 30), t, true));
            for (int c = 0; c < cycles; c++)
            {
                for (int i = 0; i < restSamples; i++, t += StepUs)
                    samples.Add(new GroundTruthSample(
                        target.X + restOffset.X + (noise.NextGaussian() * 9),
                        target.Y + restOffset.Y + (noise.NextGaussian() * 9), t, true));
                for (int i = 0; i < holdSamples; i++, t += StepUs)
                    samples.Add(new GroundTruthSample(
                        target.X + bias.X + (noise.NextGaussian() * 9),
                        target.Y + bias.Y + (noise.NextGaussian() * 9), t, true));
            }
            windows.Add(new GroundTruthWindow(id, target.X, target.Y, from, null, t - StepUs, samples.Count) { Samples = samples });
        }
        return Session(windows);
    }
}
