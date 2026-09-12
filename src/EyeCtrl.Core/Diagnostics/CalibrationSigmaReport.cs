namespace EyeCtrl.Core.Diagnostics;

using System.Globalization;
using System.Text;
using EyeCtrl.Core.Calibration;

/// <summary>
/// Extracts the per-target noise (σ) a calibration session already measured, and splits it by where on
/// the screen the target sat.
/// <para>
/// This is the number every σ-dependent claim in the gaze-jitter debate is gated on, and it has been
/// sitting unread in <c>calibration.json</c> the whole time (σ became an IN-BAND figure in 2026-09-13,
/// so it now measures stillness on the target rather than motion around it; coverage is reported
/// beside it, and the two must be read together): <see cref="CalibrationPoint.StdDevX"/> and
/// <see cref="CalibrationPoint.StdDevY"/> are the raw per-axis standard deviations of the samples
/// collected while the user held each target. Nothing new has to be measured to obtain them — only
/// read and summarised.
/// </para>
/// <para>
/// The centre-versus-corner split is what separates DEVICE noise from MAPPING error. Trackers are
/// typically worst at the extremes of their field; if corner σ dwarfs centre σ, the problem is
/// geometry and calibration rather than a noise floor a filter could lower, and the filter work is
/// aimed at the wrong thing.
/// </para>
/// <para>Read-only: it computes over persisted data and changes nothing.</para>
/// </summary>
public static class CalibrationSigmaReport
{
    /// <summary>Where a target sat on screen. The layout places targets at 20 %, 50 % and 80 % of each axis.</summary>
    public enum Zone
    {
        /// <summary>Both axes near the middle.</summary>
        Center,
        /// <summary>Both axes near an extreme — the hardest case for the tracker.</summary>
        Corner,
        /// <summary>One axis extreme, one central.</summary>
        Edge,
    }

    /// <summary>One target's measured noise.</summary>
    /// <param name="Zone">Where the target sat.</param>
    /// <param name="X">Target X in physical pixels.</param>
    /// <param name="Y">Target Y in physical pixels.</param>
    /// <param name="SigmaX">Per-axis standard deviation of the samples held on it, in pixels.</param>
    /// <param name="SigmaY">Per-axis standard deviation, Y.</param>
    /// <param name="SampleCount">In-band samples the mean and σ were computed from.</param>
    /// <param name="ErrorPixels">Distance from target to mean gaze — accuracy, as against σ's precision.</param>
    /// <param name="Coverage">
    /// Fraction of this target's attempt spent inside the band, or null when the calibration predates
    /// the measurement. σ is computed over in-band samples ONLY, so it cannot be read without this:
    /// three samples out of three hundred give a tight σ that describes nothing. A low σ paired with
    /// low coverage is the signature of the 2026-09-12 failure, and reporting σ alone is what let it
    /// pass as precision.
    /// </param>
    public readonly record struct TargetSigma(
        Zone Zone, double X, double Y, double SigmaX, double SigmaY, int SampleCount, double ErrorPixels,
        double? Coverage = null)
    {
        /// <summary>Radial σ: the two axes combined into one number, √(σx² + σy²).</summary>
        public double SigmaRadial => Math.Sqrt(SigmaX * SigmaX + SigmaY * SigmaY);
    }

    /// <summary>The summary of a calibration's measured noise.</summary>
    /// <param name="Targets">Per-target detail, in the order the session presented them.</param>
    /// <param name="RmsSigmaRadial">Root-mean-square radial σ across all targets.</param>
    /// <param name="MedianSigmaRadial">Median radial σ — the robust counterpart, unmoved by one bad target.</param>
    /// <param name="CenterSigmaRadial">Mean radial σ over centre targets, or null when the layout had none.</param>
    /// <param name="CornerSigmaRadial">Mean radial σ over corner targets, or null when there were none.</param>
    /// <param name="MinCoverage">
    /// The worst per-target coverage, or null when no target recorded one. The MINIMUM rather than the
    /// mean: a calibration is fitted through every point, so one target built from wander bends the
    /// whole transform, and an average would let four good targets hide it.
    /// </param>
    public sealed record Report(
        IReadOnlyList<TargetSigma> Targets,
        double RmsSigmaRadial,
        double MedianSigmaRadial,
        double? CenterSigmaRadial,
        double? CornerSigmaRadial,
        double? MinCoverage = null)
    {
        /// <summary>
        /// How much worse the corners are than the centre, as a ratio. Null when the layout lacked
        /// either zone, or when centre σ is zero and the ratio would be meaningless.
        /// </summary>
        public double? CornerToCenterRatio =>
            CenterSigmaRadial is > 0 and { } center && CornerSigmaRadial is { } corner
                ? corner / center
                : null;

        /// <summary>Renders the summary as one log-friendly line.</summary>
        public string Describe()
        {
            if (Targets.Count == 0) return "calibration sigma: no targets";

            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture,
                $"calibration sigma: n={Targets.Count} rms={RmsSigmaRadial:0.##}px median={MedianSigmaRadial:0.##}px");

            // Immediately after the σ figures, not appended as a footnote: the two are one reading, and
            // a reader who stops at "rms=3px" without seeing "coverage 4%" has drawn the wrong
            // conclusion from a line that looked complete.
            if (MinCoverage is { } cov)
            {
                sb.Append(CultureInfo.InvariantCulture, $" worst-coverage={cov * 100:0}%");
                if (cov < 0.5)
                    sb.Append(" — MOST SAMPLES WERE OFF TARGET; this sigma describes only the few that were not");
            }
            else
            {
                sb.Append(" worst-coverage=unknown (calibration predates coverage measurement)");
            }

            if (CenterSigmaRadial is { } c) sb.Append(CultureInfo.InvariantCulture, $" center={c:0.##}px");
            if (CornerSigmaRadial is { } k) sb.Append(CultureInfo.InvariantCulture, $" corner={k:0.##}px");
            if (CornerToCenterRatio is { } ratio)
            {
                sb.Append(CultureInfo.InvariantCulture, $" corner/center={ratio:0.##}×");
                sb.Append(ratio >= 2.0
                    ? " — corners are much noisier than the centre: suspect mapping/geometry before the noise floor"
                    : " — noise is broadly uniform across the screen");
            }
            return sb.ToString();
        }

        /// <summary>Renders per-target detail, one line per target.</summary>
        public string DescribeTargets()
        {
            var sb = new StringBuilder();
            foreach (var t in Targets)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(CultureInfo.InvariantCulture,
                    $"{t.Zone} ({t.X:0},{t.Y:0}) sigma=({t.SigmaX:0.##},{t.SigmaY:0.##}) radial={t.SigmaRadial:0.##} err={t.ErrorPixels:0.#}px n={t.SampleCount}");
                sb.Append(t.Coverage is { } cov
                    ? string.Create(CultureInfo.InvariantCulture, $" coverage={cov * 100:0}%")
                    : " coverage=?");
            }
            return sb.Length == 0 ? "(no targets)" : sb.ToString();
        }
    }

    /// <summary>Builds the report from a persisted calibration.</summary>
    /// <param name="data">The calibration to summarise; its <c>Points</c> carry the measured σ.</param>
    public static Report Build(CalibrationData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Points.Length == 0 || data.ScreenWidth <= 0 || data.ScreenHeight <= 0)
            return new Report(Array.Empty<TargetSigma>(), 0, 0, null, null);

        var targets = data.Points
            .Select(p => new TargetSigma(
                Classify(p.Target.X / data.ScreenWidth, p.Target.Y / data.ScreenHeight),
                p.Target.X, p.Target.Y, p.StdDevX, p.StdDevY, p.SampleCount, p.EuclideanError(),
                p.Coverage))
            .ToArray();

        var radial = targets.Select(t => t.SigmaRadial).ToArray();

        // Null when NO target carries a coverage figure (a legacy calibration). A mix is reported on
        // what is known: a single target measured at 4% is the finding, whatever the others lack.
        var known = targets.Where(t => t.Coverage is not null).Select(t => t.Coverage!.Value).ToArray();

        return new Report(
            targets,
            RmsSigmaRadial: Math.Sqrt(radial.Sum(r => r * r) / radial.Length),
            MedianSigmaRadial: Median(radial),
            CenterSigmaRadial: MeanOrNull(targets, Zone.Center),
            CornerSigmaRadial: MeanOrNull(targets, Zone.Corner),
            MinCoverage: known.Length == 0 ? null : known.Min());
    }

    // Normalised thresholds rather than pixel ones so the classification survives any screen size.
    // The layout puts targets at 0.2 / 0.5 / 0.8 of each axis, so a third either side separates them
    // cleanly without depending on the exact margin constant.
    private static Zone Classify(double normalizedX, double normalizedY)
    {
        bool outerX = normalizedX <= 1.0 / 3.0 || normalizedX >= 2.0 / 3.0;
        bool outerY = normalizedY <= 1.0 / 3.0 || normalizedY >= 2.0 / 3.0;

        if (outerX && outerY) return Zone.Corner;
        if (!outerX && !outerY) return Zone.Center;
        return Zone.Edge;
    }

    private static double? MeanOrNull(IReadOnlyList<TargetSigma> targets, Zone zone)
    {
        double sum = 0;
        int count = 0;
        foreach (var t in targets)
        {
            if (t.Zone != zone) continue;
            sum += t.SigmaRadial;
            count++;
        }
        return count == 0 ? null : sum / count;
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
