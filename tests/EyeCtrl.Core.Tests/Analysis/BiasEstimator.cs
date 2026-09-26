namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// Offline estimator for the measure-only bias probe: from a self-contained ground-truth sidecar,
/// recover how far the user's gaze sits from the position they were asked to look at.
/// <para>
/// <b>What it must not do.</b> The two obvious shortcuts both reintroduce the censoring the probe
/// exists to remove. A NEAREST-TO-TARGET tie-break picks whichever cluster sits closest to the
/// target, which at a 300 px bias is a 250 px glance rather than the hold — and then reports the
/// glance. A BAND GATE discards samples farther than the tolerance from the target, which throws away
/// the whole true cluster as soon as the bias approaches the band, leaving only the glance — the same
/// dynamic that makes the passive dwell log read 10 % at b = 120 px and nothing at all past 150 px.
/// Both are covered by tests that turn red under exactly those mutations.
/// </para>
/// <para>
/// <b>What it does instead.</b> For each target window it extracts I-DT clusters (consecutive valid
/// samples whose bounding-box diagonal stays within <see cref="DispersionPx"/>), ignores clusters
/// that start in the first <see cref="OnsetGuardMs"/> ms after the target appeared (that is the
/// saccade landing and the search), and then picks, per target, the LARGEST cluster whose bias vector
/// — cluster centroid minus target — agrees with the consensus the other targets support. A true bias
/// is one vector repeated at every target; a look-away is a screen position, so its vector differs at
/// every target and never collects multi-target support.
/// </para>
/// <para>
/// The samples come from the sidecar itself (<see cref="GroundTruthSample"/>), raw and uncalibrated
/// by EyeCtrl by construction: what is measured is the device-plus-user offset that is actually in
/// force, not a residual of a transform that was already applied.
/// </para>
/// <para>
/// The decision rule this feeds is next to it, in <see cref="BiasDecisionRule"/>.
/// </para>
/// </summary>
public static class BiasEstimator
{
    // ── Pinned constants, none of them a tuning knob ──────────────────────────

    /// <summary>
    /// Ignore clusters starting in the first 300 ms after the target appeared. That span is the
    /// saccade and the search, and its samples carry the previous position's momentum — the debate's
    /// "starts ≥ 300 ms after target onset".
    /// </summary>
    public const int OnsetGuardMs = 300;

    /// <summary>
    /// I-DT dispersion for cluster extraction, as a bounding-box diagonal in physical pixels. The
    /// same 150 px the replay gates use for held episodes, so "a cluster" here means what it means
    /// everywhere else in this analysis suite.
    /// </summary>
    public const double DispersionPx = 150;

    /// <summary>
    /// Fewer samples than this is not a cluster worth measuring — a saccade fragment, a blink edge,
    /// or a single stray sample. Five is one 150 ms sliver at the tracker's ~33 Hz.
    /// </summary>
    public const int MinClusterSamples = 5;

    /// <summary>
    /// How far two clusters' bias vectors may differ and still be "the same offset". Cluster
    /// centroids average the per-sample noise down to a few pixels, so this is driven by real
    /// cross-target variation, not sample noise — the debate's smallest decision step is 60 px, and
    /// this is well inside it. It is deliberately not scaled by the vector's magnitude: a small true
    /// bias must still reject a large glance, and a large one must not swallow it.
    /// </summary>
    public const double ConsistencyTolerancePx = 40;

    // ── Results ───────────────────────────────────────────────────────────────

    /// <summary>One I-DT cluster: where it sat, how many samples it held, and when it ran.</summary>
    /// <param name="Centroid">Component-wise mean of the cluster's samples.</param>
    /// <param name="SampleCount">Samples in the cluster — the size the estimator ranks by.</param>
    /// <param name="StartUs">Device timestamp of the cluster's first sample.</param>
    /// <param name="EndUs">Device timestamp of the cluster's last sample.</param>
    public sealed record Cluster(PhysicalPoint Centroid, int SampleCount, long StartUs, long EndUs);

    /// <summary>One target's measurement: every cluster found, and the one chosen.</summary>
    /// <param name="TargetId">The window's target id.</param>
    /// <param name="Target">Where the user was asked to look, in physical pixels.</param>
    /// <param name="Clusters">Candidates after the onset guard and the minimum size.</param>
    /// <param name="Chosen">The largest cluster whose vector agreed with the consensus, or null.</param>
    public sealed record Probe(int TargetId, PhysicalPoint Target, IReadOnlyList<Cluster> Clusters, Cluster? Chosen)
    {
        /// <summary>The chosen cluster's bias vector — where the gaze sat relative to the target.</summary>
        public PhysicalPoint? BiasVector => Chosen is null
            ? null
            : new PhysicalPoint(Chosen.Centroid.X - Target.X, Chosen.Centroid.Y - Target.Y);

        /// <summary>How far the chosen cluster sat from the target, in physical pixels.</summary>
        public double? DistancePx => BiasVector is { } v ? Math.Sqrt((v.X * v.X) + (v.Y * v.Y)) : null;
    }

    /// <summary>The estimated bias and the evidence behind it.</summary>
    /// <param name="Bias">
    /// The mean of the chosen clusters' bias vectors, or null when no vector was supported by at
    /// least two targets — a single target cannot be checked against "the other targets".
    /// </param>
    /// <param name="TargetsWithData">Targets whose window produced at least one eligible cluster.</param>
    /// <param name="TargetsUsed">Targets that contributed to <see cref="Bias"/>.</param>
    /// <param name="Probes">One entry per window, in presentation order.</param>
    public sealed record Estimate(
        PhysicalPoint? Bias,
        int TargetsWithData,
        int TargetsUsed,
        IReadOnlyList<Probe> Probes)
    {
        /// <summary>The magnitude of the estimated bias, in physical pixels, or null when there is none.</summary>
        public double? BiasMagnitudePx => Bias is { } b ? Math.Sqrt((b.X * b.X) + (b.Y * b.Y)) : null;

        /// <summary>
        /// The operator-facing report: the estimate first, then each target's chosen cluster size and
        /// distance — the two numbers the debate requires the probe to log, because they are what says
        /// whether the estimate rests on real holds or on scraps.
        /// </summary>
        public string Describe()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            if (Bias is { } b)
            {
                sb.Append("Bias probe — b = (").Append(b.X.ToString("0.0", ci)).Append(", ")
                  .Append(b.Y.ToString("0.0", ci)).Append(") px, |b| = ")
                  .Append(BiasMagnitudePx!.Value.ToString("0.0", ci)).Append(" px, from ")
                  .Append(TargetsUsed).Append(" of ").Append(Probes.Count).AppendLine(" targets");
            }
            else
            {
                sb.Append("Bias probe — no estimate: no cluster vector was consistent across at least two targets (")
                  .Append(TargetsWithData).Append(" of ").Append(Probes.Count).AppendLine(" targets had clusters)");
            }

            foreach (var probe in Probes)
            {
                sb.Append("  target ").Append(probe.TargetId)
                  .Append(" at (").Append(probe.Target.X.ToString("0", ci)).Append(", ")
                  .Append(probe.Target.Y.ToString("0", ci)).Append("): ");
                if (probe.Chosen is { } chosen)
                {
                    sb.Append("cluster ").Append(chosen.SampleCount).Append(" samples, distance ")
                      .Append(probe.DistancePx!.Value.ToString("0.0", ci)).Append(" px at (")
                      .Append(chosen.Centroid.X.ToString("0.0", ci)).Append(", ")
                      .Append(chosen.Centroid.Y.ToString("0.0", ci)).Append(')');
                }
                else
                {
                    sb.Append("no cluster consistent with the others (").Append(probe.Clusters.Count)
                      .Append(" candidate clusters)");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    // ── Measurement ───────────────────────────────────────────────────────────

    /// <summary>
    /// Estimates the gaze bias from a self-contained ground-truth session. Deterministic: the same
    /// sidecar gives the same estimate.
    /// </summary>
    /// <param name="session">The session whose windows carry their own raw samples.</param>
    public static Estimate Measure(GroundTruthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var probes = new List<Probe>(session.Windows.Count);
        foreach (var window in session.Windows)
            probes.Add(new Probe(window.TargetId, window.Target, ExtractClusters(window), Chosen: null));

        // Vote over every candidate vector from every target. The true bias appears once per target
        // (same vector, because it is a displacement); a look-away cluster appears once per target
        // too, but at a different vector each time — it is a position on screen, and the targets are
        // not on top of each other. So support across targets separates them without ever consulting
        // how close a cluster is to its target.
        PhysicalPoint? consensus = null;
        int bestSupport = 0, bestSupportingSamples = -1;
        for (int i = 0; i < probes.Count; i++)
        {
            foreach (var cluster in probes[i].Clusters)
            {
                var candidate = Vector(probes[i].Target, cluster.Centroid);
                int support = 0, supportingSamples = 0;
                foreach (var probe in probes)
                {
                    int largest = 0;
                    foreach (var other in probe.Clusters)
                    {
                        if (Distance(Vector(probe.Target, other.Centroid), candidate) <= ConsistencyTolerancePx)
                            largest = Math.Max(largest, other.SampleCount);
                    }
                    if (largest > 0)
                    {
                        support++;
                        supportingSamples += largest;
                    }
                }

                if (support > bestSupport
                    || (support == bestSupport && supportingSamples > bestSupportingSamples))
                {
                    bestSupport = support;
                    bestSupportingSamples = supportingSamples;
                    consensus = candidate;
                }
            }
        }

        if (consensus is null || bestSupport < 2)
            return new Estimate(null, probes.Count(p => p.Clusters.Count > 0), 0, probes);

        double sumX = 0, sumY = 0;
        int used = 0;
        var chosenProbes = new List<Probe>(probes.Count);
        foreach (var probe in probes)
        {
            Cluster? largest = null;
            foreach (var cluster in probe.Clusters)
            {
                if (Distance(Vector(probe.Target, cluster.Centroid), consensus.Value) > ConsistencyTolerancePx)
                    continue;
                if (largest is null || cluster.SampleCount > largest.SampleCount)
                    largest = cluster;
            }

            chosenProbes.Add(probe with { Chosen = largest });
            if (largest is not null)
            {
                var vector = Vector(probe.Target, largest.Centroid);
                sumX += vector.X;
                sumY += vector.Y;
                used++;
            }
        }

        var bias = new PhysicalPoint(sumX / used, sumY / used);
        return new Estimate(bias, chosenProbes.Count(p => p.Clusters.Count > 0), used, chosenProbes);
    }

    /// <summary>
    /// Extracts I-DT clusters from one window's own samples: consecutive VALID samples whose
    /// bounding-box diagonal stays within <see cref="DispersionPx"/>, starting no earlier than
    /// <see cref="OnsetGuardMs"/> after the target appeared, and kept only at
    /// <see cref="MinClusterSamples"/> or more.
    /// <para>
    /// No distance-to-target test appears anywhere in here, deliberately — that is the band gate
    /// this estimator must not have.
    /// </para>
    /// </summary>
    /// <param name="window">The target window, carrying its own raw samples.</param>
    public static List<Cluster> ExtractClusters(GroundTruthWindow window)
    {
        var clusters = new List<Cluster>();
        var xs = new List<double>();
        var ys = new List<double>();
        long startUs = 0, lastUs = 0;
        double minX = 0, maxX = 0, minY = 0, maxY = 0;
        long earliestStartUs = window.DisplayedFromUs + (OnsetGuardMs * 1000L);

        void Close()
        {
            if (xs.Count >= MinClusterSamples)
            {
                double sumX = 0, sumY = 0;
                foreach (var x in xs) sumX += x;
                foreach (var y in ys) sumY += y;
                clusters.Add(new Cluster(
                    new PhysicalPoint(sumX / xs.Count, sumY / ys.Count), xs.Count, startUs, lastUs));
            }
            xs.Clear();
            ys.Clear();
        }

        foreach (var sample in window.Samples)
        {
            if (!sample.Valid) continue;
            if (sample.TimestampUs < earliestStartUs || sample.TimestampUs > window.EndedAtUs) continue;

            if (xs.Count == 0)
            {
                xs.Add(sample.X);
                ys.Add(sample.Y);
                startUs = sample.TimestampUs;
                minX = maxX = sample.X;
                minY = maxY = sample.Y;
            }
            else
            {
                double nextMinX = Math.Min(minX, sample.X), nextMaxX = Math.Max(maxX, sample.X);
                double nextMinY = Math.Min(minY, sample.Y), nextMaxY = Math.Max(maxY, sample.Y);
                if (BoxDiagonal(nextMinX, nextMaxX, nextMinY, nextMaxY) <= DispersionPx)
                {
                    xs.Add(sample.X);
                    ys.Add(sample.Y);
                    minX = nextMinX;
                    maxX = nextMaxX;
                    minY = nextMinY;
                    maxY = nextMaxY;
                }
                else
                {
                    Close();
                    xs.Add(sample.X);
                    ys.Add(sample.Y);
                    startUs = sample.TimestampUs;
                    minX = maxX = sample.X;
                    minY = maxY = sample.Y;
                }
            }

            lastUs = sample.TimestampUs;
        }

        Close();
        return clusters;
    }

    private static PhysicalPoint Vector(PhysicalPoint target, PhysicalPoint centroid) =>
        new(centroid.X - target.X, centroid.Y - target.Y);

    private static double Distance(PhysicalPoint a, PhysicalPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double BoxDiagonal(double minX, double maxX, double minY, double maxY)
    {
        double w = maxX - minX, h = maxY - minY;
        return Math.Sqrt((w * w) + (h * h));
    }
}
