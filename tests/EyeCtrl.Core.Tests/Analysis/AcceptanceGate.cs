namespace EyeCtrl.Core.Tests.Analysis;

using System.Text;

/// <summary>
/// The debate's pre-registered acceptance gate, executable: given a baseline and a candidate measured
/// on the SAME input, it says pass or fail per criterion and overall.
/// <para>
/// Pre-registered means the thresholds were fixed before any candidate existed, which is the only
/// thing that stops a gate from being retro-fitted to whatever the new filter happens to achieve. They
/// are therefore constants here, and a change to one is a change to the debate's conclusion — not a
/// tuning knob.
/// </para>
/// <para>
/// Two criteria are vetoes: latency and dwell. A candidate that regresses either FAILS regardless of
/// how far the deviation number fell. Stability is scored pre-stabilization, responsiveness post.
/// </para>
/// </summary>
public static class AcceptanceGate
{
    /// <summary>p95 radial fixation deviation must fall by at least this fraction.</summary>
    public const double MinDeviationReduction = 0.60;

    /// <summary>Duration-bucketed pops must fall by at least this fraction.</summary>
    public const double MinPopReduction = 0.80;

    /// <summary>90 % step latency ceiling, in milliseconds. A veto.</summary>
    public const double MaxStepLatencyMs = 150;

    /// <summary>Dwell time-to-activate may not grow by more than this many milliseconds. A veto.</summary>
    public const double MaxDwellTimeIncreaseMs = 50;

    /// <summary>Dwell-progress resets per minute must fall by at least this fraction.</summary>
    public const double MinDwellResetReduction = 0.80;

    /// <summary>The measurements one candidate produced on one recording.</summary>
    /// <param name="Name">Candidate label.</param>
    /// <param name="P95DeviationPixels">p95 radial fixation deviation.</param>
    /// <param name="PopCount">Duration-bucketed pop count (all buckets).</param>
    /// <param name="StepLatencyMs">90 % step latency, or null when the stream contained no measurable step.</param>
    /// <param name="DwellTimeToActivateMs">Replay dwell time-to-activate, or null when it never activated.</param>
    /// <param name="DwellResetsPerMinute">Replay dwell-progress resets per minute.</param>
    public sealed record Measurement(
        string Name,
        double P95DeviationPixels,
        int PopCount,
        double? StepLatencyMs,
        double? DwellTimeToActivateMs,
        double DwellResetsPerMinute);

    /// <summary>One criterion's verdict.</summary>
    /// <param name="Criterion">Human-readable name.</param>
    /// <param name="Passed">Whether it met its threshold.</param>
    /// <param name="Detail">The numbers behind the verdict, for the report.</param>
    /// <param name="IsVeto">Whether failing it rejects the candidate outright.</param>
    public sealed record Verdict(string Criterion, bool Passed, string Detail, bool IsVeto);

    /// <summary>The full result: per-criterion verdicts and the overall decision.</summary>
    /// <param name="Candidate">Candidate label.</param>
    /// <param name="Verdicts">One entry per criterion, in gate order.</param>
    /// <param name="Accepted">True only when every criterion passed.</param>
    public sealed record GateResult(string Candidate, IReadOnlyList<Verdict> Verdicts, bool Accepted)
    {
        /// <summary>Renders the result as a plain-text report suitable for pasting into a ticket.</summary>
        public string ToReport()
        {
            var sb = new StringBuilder();
            sb.Append("Acceptance gate — ").Append(Candidate).Append(": ")
              .AppendLine(Accepted ? "ACCEPTED" : "REJECTED");
            foreach (var v in Verdicts)
            {
                sb.Append("  [").Append(v.Passed ? "pass" : "FAIL").Append(']')
                  .Append(v.IsVeto ? " (veto) " : " ")
                  .Append(v.Criterion).Append(" — ").AppendLine(v.Detail);
            }
            return sb.ToString();
        }
    }

    /// <summary>Evaluates a candidate against the baseline it must improve on.</summary>
    public static GateResult Evaluate(Measurement baseline, Measurement candidate)
    {
        var verdicts = new List<Verdict>
        {
            Reduction("p95 radial fixation deviation", baseline.P95DeviationPixels,
                      candidate.P95DeviationPixels, MinDeviationReduction, "px"),

            Reduction("duration-bucketed pops", baseline.PopCount,
                      candidate.PopCount, MinPopReduction, ""),

            Ceiling("90 % step latency", candidate.StepLatencyMs, MaxStepLatencyMs, "ms"),

            DwellTime(baseline.DwellTimeToActivateMs, candidate.DwellTimeToActivateMs),

            Reduction("dwell-progress resets per minute", baseline.DwellResetsPerMinute,
                      candidate.DwellResetsPerMinute, MinDwellResetReduction, "/min"),
        };

        return new GateResult(candidate.Name, verdicts, verdicts.All(v => v.Passed));
    }

    private static Verdict Reduction(string name, double baseline, double candidate, double required, string unit)
    {
        // A baseline of zero cannot be reduced by 60 %. Treating that as a pass would let a candidate
        // clear a criterion the recording never exercised, so it is reported as inapplicable-but-passing
        // ONLY when the candidate is also zero; a candidate that introduced the problem fails.
        if (baseline <= 0)
        {
            bool ok = candidate <= 0;
            return new Verdict(name, ok,
                $"baseline {baseline:0.##}{unit} — criterion not exercised by this recording; candidate {candidate:0.##}{unit}",
                IsVeto: false);
        }

        double reduction = (baseline - candidate) / baseline;
        return new Verdict(name, reduction >= required,
            $"{baseline:0.##}{unit} → {candidate:0.##}{unit} ({reduction:P0} down, need {required:P0})",
            IsVeto: false);
    }

    private static Verdict Ceiling(string name, double? value, double ceiling, string unit)
    {
        if (value is not { } v)
            return new Verdict(name, false, "not measurable on this recording — a veto criterion cannot be waived", IsVeto: true);
        return new Verdict(name, v <= ceiling, $"{v:0.##}{unit} (ceiling {ceiling:0.##}{unit})", IsVeto: true);
    }

    private static Verdict DwellTime(double? baseline, double? candidate)
    {
        const string name = "dwell time-to-activate";
        if (baseline is not { } b || candidate is not { } c)
            return new Verdict(name, false, "dwell did not activate on one of the runs — a veto criterion cannot be waived", IsVeto: true);

        double delta = c - b;
        return new Verdict(name, delta <= MaxDwellTimeIncreaseMs,
            $"{b:0.##}ms → {c:0.##}ms ({delta:+0.##;-0.##;0}ms, allowance +{MaxDwellTimeIncreaseMs:0}ms)",
            IsVeto: true);
    }
}
