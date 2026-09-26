namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// Offline estimator for the measure-only bias probe: from a self-contained ground-truth sidecar,
/// recover where the user's gaze sat relative to each target they were asked to look at.
/// <para>
/// <b>What it must not do.</b> Two shortcuts reintroduce the censoring the probe exists to remove. A
/// NEAREST-TO-TARGET tie-break picks whichever cluster sits closest to the target, which at a 300 px
/// bias is a 250 px glance rather than the hold — and then reports the glance. A BAND GATE discards
/// samples farther than the tolerance from the target, which throws away the whole true cluster as
/// soon as the bias approaches the band, leaving only the glance. Both are pinned by tests that turn
/// red under exactly those mutations.
/// </para>
/// <para>
/// <b>What it does instead.</b> Per target window it extracts I-DT clusters (consecutive valid
/// samples whose bounding-box diagonal stays within <see cref="DispersionPx"/>), ignores clusters
/// starting in the first <see cref="OnsetGuardMs"/> ms after the target appeared, and then searches
/// for the best set of one-cluster-per-target that is consistent with a SMOOTH FIELD across targets:
/// an affine map from screen position to offset, fitted to the chosen clusters, whose residual at
/// every chosen cluster is within <see cref="FieldResidualPx"/>. Consistency therefore VETOES
/// look-aways (a screen position, not a displacement — it does not fit the field) without forcing the
/// offset to be one constant translation, so a position-dependent residual (corner degradation, a
/// scale error) is measured rather than silently dropped.
/// </para>
/// <para>
/// <b>Two identical solutions are not a decision.</b> A look-away can itself be a consistent field —
/// e.g. the user rests on the ring's top edge at every target. When a rival field of the same size is
/// comparably supported, the result is flagged <see cref="Estimate.Ambiguous"/> and the decision rule
/// answers Inconclusive instead of picking one.
/// </para>
/// <para>
/// The samples come from the sidecar itself (<see cref="GroundTruthSample"/>), raw and uncalibrated
/// by EyeCtrl by construction: what is measured is the device-plus-user offset actually in force.
/// The decision rule is next to it, in <see cref="BiasDecisionRule"/>.
/// </para>
/// </summary>
public static class BiasEstimator
{
    // ── Pinned constants, none of them a tuning knob ──────────────────────────

    /// <summary>
    /// Ignore clusters starting in the first 300 ms after the target appeared. That span is the
    /// saccade and the search, and its samples carry the previous position's momentum.
    /// </summary>
    public const int OnsetGuardMs = 300;

    /// <summary>
    /// I-DT dispersion for cluster extraction, as a bounding-box diagonal in physical pixels.
    /// <para>
    /// Deliberately tighter than the replay gates' 150 px episode dispersion. At 150 a glance landing
    /// within about 100 px of the hold is dragged into the hold's cluster, and the cluster centroid
    /// lands between the two — measured at 42 px of estimate error on the reviewers' P6 stream. The
    /// thresholds this estimate feeds are 60 and 75 px, so a 150 px cluster is too coarse a ruler.
    /// </para>
    /// </summary>
    public const double DispersionPx = 75;

    /// <summary>
    /// Fewer samples than this is not a cluster worth measuring — a saccade fragment, a blink edge,
    /// or a single stray sample. Five is one 150 ms sliver at the tracker's ~33 Hz.
    /// </summary>
    public const int MinClusterSamples = 5;

    /// <summary>
    /// How far a chosen cluster's offset may sit from the smooth field fitted to the chosen clusters.
    /// The field absorbs real position-dependence (tens of px across this display); a look-away is a
    /// different place on screen, hundreds. 30 px leaves room for a modestly non-linear real field
    /// while rejecting the mixed hold/look-away assignments that made a 200-seed run exceed the 20 px
    /// bar tens of times at 50 px.
    /// </summary>
    public const double FieldResidualPx = 25;

    /// <summary>
    /// Two valid fields are rivals when the second is supported by at least this fraction of the
    /// first's samples — "comparable support" — and their mean offsets differ by more than
    /// <see cref="AmbiguitySeparationPx"/>. A factor of two (0.5) is the widest that still calls the
    /// reviewers' 8-vs-9 sample distractor comparable; it is meant to catch a user who rests on two
    /// places rather than to rank two ways of measuring one place.
    /// </summary>
    public const double AmbiguitySampleRatio = 0.5;

    /// <summary>
    /// Rivals closer than this are the same place measured twice (a split cluster's sub-fragments)
    /// and must not be reported as ambiguous. Real alternatives differ by the distance between two
    /// screen positions, not by cluster noise.
    /// </summary>
    public const double AmbiguitySeparationPx = 40;

    /// <summary>
    /// Clusters whose centroids sit within this many pixels are the SAME PLACE revisited — a stream
    /// that returns to its hold ten times produces ten clusters there, and ranking them as ten
    /// candidates would both bury a smaller but distinct look-away under the cap and split one
    /// place's evidence into ten weak votes. They are merged into one place with the summed samples
    /// before <see cref="MaxClustersPerTarget"/> applies.
    /// </summary>
    public const double PlaceMergePx = 40;

    /// <summary>
    /// Only this many places per target enter the search, by sample count. The truth is one of the
    /// largest; the cap keeps the assignment search (choices^targets) tiny. Reported in the
    /// description when it bites.
    /// </summary>
    public const int MaxClustersPerTarget = 3;

    // ── Results ───────────────────────────────────────────────────────────────

    /// <summary>One I-DT cluster: where it sat, how many samples it held, and how tightly.</summary>
    /// <param name="Centroid">Component-wise mean of the cluster's samples.</param>
    /// <param name="SampleCount">Samples in the cluster — the size the estimator ranks by.</param>
    /// <param name="StartUs">Device timestamp of the cluster's first sample.</param>
    /// <param name="EndUs">Device timestamp of the cluster's last sample.</param>
    /// <param name="DispersionPx">The cluster's bounding-box diagonal, for the operator's eye.</param>
    public sealed record Cluster(
        PhysicalPoint Centroid, int SampleCount, long StartUs, long EndUs, double DispersionPx);

    /// <summary>One target's measurement: every cluster found, and the one chosen by the field.</summary>
    /// <param name="TargetId">The window's target id.</param>
    /// <param name="Target">Where the user was asked to look, in physical pixels.</param>
    /// <param name="Clusters">Candidates after the onset guard, the minimum size and the top-N cap.</param>
    /// <param name="Chosen">The cluster the best consistent field selects for this target, or null.</param>
    public sealed record Probe(int TargetId, PhysicalPoint Target, IReadOnlyList<Cluster> Clusters, Cluster? Chosen)
    {
        /// <summary>The chosen cluster's bias vector — where the gaze sat relative to the target.</summary>
        public PhysicalPoint? BiasVector => Chosen is null
            ? null
            : new PhysicalPoint(Chosen.Centroid.X - Target.X, Chosen.Centroid.Y - Target.Y);

        /// <summary>How far the chosen cluster sat from the target, in physical pixels.</summary>
        public double? DistancePx => BiasVector is { } v ? Math.Sqrt((v.X * v.X) + (v.Y * v.Y)) : null;

        /// <summary>
        /// The largest distance any of this target's clusters sits from the target — what the decision
        /// rule reports for a target whose clusters were all rejected, so "clusters disagree" names a
        /// number instead of saying "not measured".
        /// </summary>
        public double? LargestClusterDistancePx => Clusters.Count == 0
            ? null
            : Clusters.Max(c => Math.Sqrt(
                ((c.Centroid.X - Target.X) * (c.Centroid.X - Target.X))
                + ((c.Centroid.Y - Target.Y) * (c.Centroid.Y - Target.Y))));
    }

    /// <summary>A rival field: a second consistent solution with comparable support.</summary>
    /// <param name="Bias">Its mean offset across the targets it uses.</param>
    /// <param name="Targets">How many targets it uses.</param>
    /// <param name="Samples">Total samples supporting it.</param>
    public sealed record Alternative(PhysicalPoint Bias, int Targets, int Samples);

    /// <summary>The estimated bias field's evidence.</summary>
    /// <param name="Bias">
    /// The mean offset across the chosen clusters, or null when no consistent assignment was found.
    /// Position-dependent fields are described by the per-target <see cref="Probe.BiasVector"/>s; this
    /// mean is the single-number summary the acceptance stream's tolerance is stated against.
    /// </param>
    /// <param name="TargetsWithData">Targets whose window produced at least one eligible cluster.</param>
    /// <param name="TargetsUsed">Targets selected by the best consistent field.</param>
    /// <param name="Probes">One entry per window, in presentation order.</param>
    /// <param name="Ambiguous">
    /// True when a rival field uses the same number of targets with comparable sample support at a
    /// materially different offset. The rule answers Inconclusive — picking one would be a guess.
    /// </param>
    /// <param name="Rival">The best such rival, or null.</param>
    /// <param name="MaxFieldResidualPx">The chosen field's worst residual at a chosen cluster, or null.</param>
    public sealed record Estimate(
        PhysicalPoint? Bias,
        int TargetsWithData,
        int TargetsUsed,
        IReadOnlyList<Probe> Probes,
        bool Ambiguous,
        Alternative? Rival,
        double? MaxFieldResidualPx)
    {
        /// <summary>The magnitude of the estimated bias, in physical pixels, or null.</summary>
        public double? BiasMagnitudePx => Bias is { } b ? Math.Sqrt((b.X * b.X) + (b.Y * b.Y)) : null;

        /// <summary>
        /// The operator-facing report: the estimate first, then each target's chosen cluster size,
        /// distance and dispersion — the numbers the debate requires the probe to log, because they
        /// are what says whether the estimate rests on real holds or on scraps.
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
                  .Append(TargetsUsed).Append(" of ").Append(Probes.Count).Append(" targets");
                if (MaxFieldResidualPx is { } residual)
                    sb.Append(", field residual ≤ ").Append(residual.ToString("0.0", ci)).Append(" px");
                sb.AppendLine();
            }
            else
            {
                sb.Append("Bias probe — no consistent field: ")
                  .Append(TargetsWithData).Append(" of ").Append(Probes.Count)
                  .AppendLine(" targets had clusters, but no set of them fit a smooth offset field");
            }

            if (Ambiguous && Rival is { } rival)
            {
                sb.Append("  AMBIGUOUS — a rival field at (").Append(rival.Bias.X.ToString("0.0", ci))
                  .Append(", ").Append(rival.Bias.Y.ToString("0.0", ci)).Append(") px from ")
                  .Append(rival.Targets).Append(" targets (").Append(rival.Samples)
                  .AppendLine(" samples) is comparably supported");
            }

            foreach (var probe in Probes)
            {
                sb.Append("  target ").Append(probe.TargetId)
                  .Append(" at (").Append(probe.Target.X.ToString("0", ci)).Append(", ")
                  .Append(probe.Target.Y.ToString("0", ci)).Append("): ");
                if (probe.Chosen is { } chosen)
                {
                    sb.Append("cluster ").Append(chosen.SampleCount).Append(" samples, distance ")
                      .Append(probe.DistancePx!.Value.ToString("0.0", ci)).Append(" px, dispersion ")
                      .Append(chosen.DispersionPx.ToString("0.0", ci)).Append(" px at (")
                      .Append(chosen.Centroid.X.ToString("0.0", ci)).Append(", ")
                      .Append(chosen.Centroid.Y.ToString("0.0", ci)).Append(')');
                }
                else if (probe.Clusters.Count > 0)
                {
                    sb.Append("clusters disagree with the field (largest ")
                      .Append(probe.LargestClusterDistancePx!.Value.ToString("0.0", ci)).Append(" px)")
                      .Append(", ").Append(probe.Clusters.Count).Append(" candidate clusters");
                }
                else
                {
                    sb.Append("no clusters (window has no steady hold)");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    // ── Measurement ───────────────────────────────────────────────────────────

    /// <summary>
    /// Estimates the bias field from a self-contained ground-truth session. Deterministic: the same
    /// sidecar gives the same estimate.
    /// </summary>
    /// <param name="session">The session whose windows carry their own raw samples.</param>
    public static Estimate Measure(GroundTruthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var probes = new List<Probe>(session.Windows.Count);
        foreach (var window in session.Windows)
            probes.Add(new Probe(window.TargetId, window.Target, ExtractClusters(window), Chosen: null));

        var best = FindBestField(probes);
        if (best is null)
        {
            return new Estimate(null, probes.Count(p => p.Clusters.Count > 0), 0, probes,
                Ambiguous: false, Rival: null, MaxFieldResidualPx: null);
        }

        var chosen = new List<Probe>(probes.Count);
        double sumX = 0, sumY = 0;
        for (int i = 0; i < probes.Count; i++)
        {
            var cluster = best.Choices[i];
            chosen.Add(probes[i] with { Chosen = cluster });
            if (cluster is not null)
            {
                sumX += cluster.Centroid.X - probes[i].Target.X;
                sumY += cluster.Centroid.Y - probes[i].Target.Y;
            }
        }

        var bias = new PhysicalPoint(sumX / best.Targets, sumY / best.Targets);
        var rival = FindBestRival(probes, best);
        return new Estimate(bias, chosen.Count(p => p.Clusters.Count > 0), best.Targets, chosen,
            Ambiguous: rival is not null, rival, best.MaxResidualPx);
    }

    /// <summary>One candidate solution: which cluster each probe contributes (null = target unused).</summary>
    private sealed record FieldSolution(Cluster?[] Choices, int Targets, int Samples, double MaxResidualPx)
    {
        public PhysicalPoint Mean(IReadOnlyList<Probe> probes)
        {
            double x = 0, y = 0;
            int n = 0;
            for (int i = 0; i < probes.Count; i++)
            {
                if (Choices[i] is not { } cluster) continue;
                x += cluster.Centroid.X - probes[i].Target.X;
                y += cluster.Centroid.Y - probes[i].Target.Y;
                n++;
            }
            return n == 0 ? new PhysicalPoint(0, 0) : new PhysicalPoint(x / n, y / n);
        }
    }

    /// <summary>
    /// Exhaustively searches one-cluster-per-target assignments and returns the valid one using the
    /// most targets (and, among those, the most samples). A valid assignment of three or more clusters
    /// must fit a smooth field within <see cref="FieldResidualPx"/>; one- and two-cluster assignments
    /// carry no field evidence and are allowed but cannot on their own justify a "No trigger" verdict.
    /// </summary>
    private static FieldSolution? FindBestField(IReadOnlyList<Probe> probes)
    {
        FieldSolution? best = null;
        foreach (var candidate in Enumerate(probes))
        {
            if (candidate.Targets == 0) continue;
            if (best is null
                || candidate.Targets > best.Targets
                || (candidate.Targets == best.Targets && candidate.Samples > best.Samples))
            {
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>
    /// The strongest rival: a different valid assignment using the same number of targets with at
    /// least <see cref="AmbiguitySampleRatio"/> of the winner's samples, whose mean offset differs by
    /// more than <see cref="AmbiguitySeparationPx"/>. Null when there is no such alternative.
    /// </summary>
    private static Alternative? FindBestRival(IReadOnlyList<Probe> probes, FieldSolution best)
    {
        if (best.Targets < 2) return null;

        var bestMean = best.Mean(probes);
        FieldSolution? rival = null;
        foreach (var candidate in Enumerate(probes))
        {
            if (candidate.Targets != best.Targets) continue;
            if (!DiffersFrom(candidate, best, probes)) continue;
            if (candidate.Samples < best.Samples * AmbiguitySampleRatio) continue;
            if (Distance(candidate.Mean(probes), bestMean) <= AmbiguitySeparationPx) continue;
            if (rival is null || candidate.Samples > rival.Samples) rival = candidate;
        }
        return rival is null ? null : new Alternative(rival.Mean(probes), rival.Targets, rival.Samples);
    }

    private static bool DiffersFrom(FieldSolution a, FieldSolution b, IReadOnlyList<Probe> probes)
    {
        for (int i = 0; i < probes.Count; i++)
            if (!ReferenceEquals(a.Choices[i], b.Choices[i]))
                return true;
        return false;
    }

    /// <summary>Enumerates every assignment of zero-or-one cluster per probe whose chosen clusters fit a smooth field.</summary>
    private static IEnumerable<FieldSolution> Enumerate(IReadOnlyList<Probe> probes)
    {
        int n = probes.Count;
        var choices = new Cluster?[n];
        var counters = new int[n];

        while (true)
        {
            // counters[i] indexes clusters[i] for 0..count-1, and means "no cluster" at count.
            int selected = 0, samples = 0;
            for (int i = 0; i < n; i++)
            {
                int count = probes[i].Clusters.Count;
                choices[i] = counters[i] < count ? probes[i].Clusters[counters[i]] : null;
                if (choices[i] is { } cluster)
                {
                    selected++;
                    samples += cluster.SampleCount;
                }
            }

            if (selected > 0 && TryFit(choices, probes, out double maxResidual))
                yield return new FieldSolution((Cluster?[])choices.Clone(), selected, samples, maxResidual);

            // Odometer increment.
            int position = 0;
            while (position < n)
            {
                counters[position]++;
                if (counters[position] <= probes[position].Clusters.Count) break;
                counters[position] = 0;
                position++;
            }
            if (position == n) yield break;
        }
    }

    /// <summary>
    /// Field test for one assignment. Three or more clusters must fit an affine offset field within
    /// <see cref="FieldResidualPx"/>; fewer carry no field evidence and always pass (they are
    /// provisional, and <see cref="BiasDecisionRule"/> refuses to conclude "No trigger" from them).
    /// </summary>
    private static bool TryFit(Cluster?[] choices, IReadOnlyList<Probe> probes, out double maxResidual)
    {
        maxResidual = 0;
        int selected = 0;
        for (int i = 0; i < choices.Length; i++)
            if (choices[i] is not null) selected++;
        if (selected < 3) return true;

        // Normal equations for vx = a·x + b·y + c and vy = d·x + e·y + f. The position matrix is
        // shared; only the right-hand sides differ, so it is built once and solved twice.
        double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0;
        double sxVx = 0, syVx = 0, sVx = 0;
        double sxVy = 0, syVy = 0, sVy = 0;
        int n = 0;
        for (int i = 0; i < choices.Length; i++)
        {
            if (choices[i] is not { } cluster) continue;
            double x = probes[i].Target.X, y = probes[i].Target.Y;
            double vx = cluster.Centroid.X - x, vy = cluster.Centroid.Y - y;
            sxx += x * x; sxy += x * y; sx += x; syy += y * y; sy += y;
            sxVx += x * vx; syVx += y * vx; sVx += vx;
            sxVy += x * vy; syVy += y * vy; sVy += vy;
            n++;
        }

        double[,] m =
        {
            { sxx, sxy, sx },
            { sxy, syy, sy },
            { sx, sy, n },
        };
        if (!TrySolve3(m, [sxVx, syVx, sVx], out var fx)) return false;
        if (!TrySolve3(m, [sxVy, syVy, sVy], out var fy)) return false;

        for (int i = 0; i < choices.Length; i++)
        {
            if (choices[i] is not { } cluster) continue;
            double x = probes[i].Target.X, y = probes[i].Target.Y;
            double px = (fx[0] * x) + (fx[1] * y) + fx[2];
            double py = (fy[0] * x) + (fy[1] * y) + fy[2];
            double dx = px - (cluster.Centroid.X - x);
            double dy = py - (cluster.Centroid.Y - y);
            maxResidual = Math.Max(maxResidual, Math.Sqrt((dx * dx) + (dy * dy)));
        }
        return maxResidual <= FieldResidualPx;
    }

    /// <summary>Gaussian elimination with partial pivoting on a 3×3 system; false when singular.</summary>
    private static bool TrySolve3(double[,] m, double[] rhs, out double[] solution)
    {
        solution = new double[3];
        var a = new double[3, 4];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++) a[r, c] = m[r, c];
            a[r, 3] = rhs[r];
        }

        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 3; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-9) return false;
            if (pivot != col)
                for (int c = col; c < 4; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);

            for (int r = 0; r < 3; r++)
            {
                if (r == col) continue;
                double factor = a[r, col] / a[col, col];
                for (int c = col; c < 4; c++) a[r, c] -= factor * a[col, c];
            }
        }

        for (int r = 0; r < 3; r++) solution[r] = a[r, 3] / a[r, r];
        return true;
    }

    /// <summary>
    /// When a closed cluster's samples span more than this (max pairwise distance), it is split at the
    /// two centroids a 2-means pass finds — provided each side has <see cref="MinClusterSamples"/>.
    /// A pure 50-sample fixation's range is about 4σ ≈ 36 px at this user's σ ≈ 9; a hold with a glance
    /// pulled into it spans the 50 px between the two places or more. The test is on the DIAMETER, not
    /// on the 2-means centroids: a bridge of saccade samples between the modes pulls the centroids
    /// toward each other, but it cannot shrink the span. A false split of one place is harmless — the
    /// place merge puts its parts back together — while a missed split contaminates the estimate.
    /// </summary>
    public const double SplitDiameterPx = 45;

    /// <summary>
    /// When a closed cluster's samples are bimodal — two places in one run, e.g. the hold and a glance
    /// that landed close enough for the dispersion box to swallow it — it is split at the two centroids
    /// a 2-means pass finds, and only when each side has <see cref="MinClusterSamples"/> and the
    /// cluster's diameter exceeds <see cref="SplitDiameterPx"/>. Without this, a contaminated cluster's
    /// centroid lands between the two places and the estimate is tens of pixels off — reviewer A's F6,
    /// measured at 42 px.
    /// </summary>
    private static List<Cluster> SplitIfBimodal(List<double> xs, List<double> ys, List<long> timestamps)
    {
        // Seeds are the farthest-apart pair, not the temporal ends: a merged run can begin and end in
        // the same mode (hold, glances, hold), and end-seeded 2-means then splits that mode in half and
        // never separates the glance. The farthest pair is one sample from each mode whenever the run
        // is bimodal, and stays inside one mode's noise when it is not.
        int seedA = 0, seedB = 0;
        double farthest = -1;
        for (int i = 0; i < xs.Count; i++)
        for (int j = i + 1; j < xs.Count; j++)
        {
            double d = ((xs[i] - xs[j]) * (xs[i] - xs[j])) + ((ys[i] - ys[j]) * (ys[i] - ys[j]));
            if (d > farthest) { farthest = d; seedA = i; seedB = j; }
        }

        double c1x = xs[seedA], c1y = ys[seedA];
        double c2x = xs[seedB], c2y = ys[seedB];
        var owner = new int[xs.Count];

        for (int iteration = 0; iteration < 12; iteration++)
        {
            double next1x = 0, next1y = 0, next2x = 0, next2y = 0;
            int n1 = 0, n2 = 0;
            for (int i = 0; i < xs.Count; i++)
            {
                double d1 = ((xs[i] - c1x) * (xs[i] - c1x)) + ((ys[i] - c1y) * (ys[i] - c1y));
                double d2 = ((xs[i] - c2x) * (xs[i] - c2x)) + ((ys[i] - c2y) * (ys[i] - c2y));
                // Ties go to the first centroid; the pass is deterministic either way.
                owner[i] = d1 <= d2 ? 0 : 1;
                if (owner[i] == 0) { next1x += xs[i]; next1y += ys[i]; n1++; }
                else { next2x += xs[i]; next2y += ys[i]; n2++; }
            }
            if (n1 == 0 || n2 == 0) break;
            c1x = next1x / n1; c1y = next1y / n1;
            c2x = next2x / n2; c2y = next2y / n2;
        }

        int count1 = owner.Count(o => o == 0), count2 = owner.Length - count1;
        double diameter = Math.Sqrt(farthest);
        // A side too small to stand alone stays where it is: dropping it moved five seeds out of the
        // 20 px band on the random-direction sweep, while keeping the run whole contaminated only one.
        if (count1 < MinClusterSamples || count2 < MinClusterSamples || diameter <= SplitDiameterPx)
            return [Build(xs, ys, timestamps, owner, 0, both: true)];

        return
        [
            Build(xs, ys, timestamps, owner, 0, both: false),
            Build(xs, ys, timestamps, owner, 1, both: false),
        ];
    }

    /// <summary>Builds a cluster from the samples owned by one side of a 2-means split.</summary>
    private static Cluster Build(
        List<double> xs, List<double> ys, List<long> timestamps, int[] owner, int side, bool both)
    {
        double sumX = 0, sumY = 0, minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        long start = long.MaxValue, end = long.MinValue;
        int count = 0;
        for (int i = 0; i < xs.Count; i++)
        {
            if (!both && owner[i] != side) continue;
            sumX += xs[i];
            sumY += ys[i];
            minX = Math.Min(minX, xs[i]); maxX = Math.Max(maxX, xs[i]);
            minY = Math.Min(minY, ys[i]); maxY = Math.Max(maxY, ys[i]);
            start = Math.Min(start, timestamps[i]);
            end = Math.Max(end, timestamps[i]);
            count++;
        }
        return new Cluster(new PhysicalPoint(sumX / count, sumY / count), count, start, end,
            BoxDiagonal(minX, maxX, minY, maxY));
    }

    /// <summary>
    /// Extracts I-DT clusters from one window's own samples: consecutive VALID samples whose
    /// bounding-box diagonal stays within <see cref="DispersionPx"/>, starting no earlier than
    /// <see cref="OnsetGuardMs"/> after the target appeared, kept at <see cref="MinClusterSamples"/> or
    /// more, split when a run is bimodal, merged by place, and capped to the
    /// <see cref="MaxClustersPerTarget"/> largest.
    /// <para>
    /// No distance-to-target test appears anywhere in here, deliberately — that is the band gate this
    /// estimator must not have.
    /// </para>
    /// </summary>
    /// <param name="window">The target window, carrying its own raw samples.</param>
    public static List<Cluster> ExtractClusters(GroundTruthWindow window)
    {
        var clusters = new List<Cluster>();
        var xs = new List<double>();
        var ys = new List<double>();
        var timestamps = new List<long>();
        double minX = 0, maxX = 0, minY = 0, maxY = 0;
        long earliestStartUs = window.DisplayedFromUs + (OnsetGuardMs * 1000L);

        void Close()
        {
            if (xs.Count >= MinClusterSamples)
                clusters.AddRange(SplitIfBimodal(xs, ys, timestamps));
            xs.Clear();
            ys.Clear();
            timestamps.Clear();
        }

        foreach (var sample in window.Samples)
        {
            if (!sample.Valid) continue;
            if (sample.TimestampUs < earliestStartUs || sample.TimestampUs > window.EndedAtUs) continue;

            if (xs.Count == 0)
            {
                xs.Add(sample.X);
                ys.Add(sample.Y);
                timestamps.Add(sample.TimestampUs);
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
                    timestamps.Add(sample.TimestampUs);
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
                    timestamps.Add(sample.TimestampUs);
                    minX = maxX = sample.X;
                    minY = maxY = sample.Y;
                }
            }
        }

        Close();

        // Merge revisited places: ten returns to the same hold are one place with ten holds' worth of
        // evidence, not ten competing candidates. The running centroid is compared, so a slowly
        // drifting fixation stays one place while a genuine look-away (tens to hundreds of px away)
        // stays its own.
        var places = new List<Cluster>();
        foreach (var cluster in clusters.OrderBy(c => c.StartUs))
        {
            int found = -1;
            for (int i = 0; i < places.Count; i++)
            {
                if (Distance(places[i].Centroid, cluster.Centroid) <= PlaceMergePx)
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                places.Add(cluster);
                continue;
            }

            var place = places[found];
            int samples = place.SampleCount + cluster.SampleCount;
            places[found] = new Cluster(
                new PhysicalPoint(
                    ((place.Centroid.X * place.SampleCount) + (cluster.Centroid.X * cluster.SampleCount)) / samples,
                    ((place.Centroid.Y * place.SampleCount) + (cluster.Centroid.Y * cluster.SampleCount)) / samples),
                samples,
                Math.Min(place.StartUs, cluster.StartUs),
                Math.Max(place.EndUs, cluster.EndUs),
                Math.Max(place.DispersionPx, cluster.DispersionPx));
        }

        // Keep the largest few places, then restore time order so the report reads chronologically.
        return places
            .OrderByDescending(c => c.SampleCount)
            .ThenBy(c => c.StartUs)
            .Take(MaxClustersPerTarget)
            .OrderBy(c => c.StartUs)
            .ToList();
    }

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
