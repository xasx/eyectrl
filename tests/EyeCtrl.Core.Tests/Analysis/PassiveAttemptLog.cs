namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// The passive attempt log, reborn as an offline instrument: replays a stream through the real
/// <see cref="DwellDetector"/> once per screen region and counts, per region, how many times gaze
/// ENTERED the region's hit radius and how many of those attempts COMPLETED.
/// <para>
/// <b>Why entries matter.</b> The passive dwell log only ever showed completions, and completions are
/// censored by the hit radius itself: measured, at a true bias of 120 px only 10 % of toolbar dwells
/// complete, and past 150 px none do. A quiet log therefore looks exactly like a clean one. Entries
/// are not censored the same way — the user still looks where the button is, their gaze just lands
/// short — so a completion rate falling toward 10 % is the signature of a bias approaching the hit
/// radius, and it survives even when nothing completes.
/// </para>
/// <para>
/// Region filtering is the whole point of "per region": a constant bias degrades the regions that sit
/// near screen edges differently from the centre, so the pattern across regions says something a
/// single pooled rate cannot.
/// </para>
/// <para>
/// This is ANALYSIS ONLY and deliberately so: no gaze-path code, no runtime counters, no file to
/// rotate. The user's real sessions keep producing their own logs; this instrument reads a recording.
/// </para>
/// </summary>
public static class PassiveAttemptLog
{
    /// <summary>A circular screen region to watch, with the same geometry a real dwell surface uses.</summary>
    /// <param name="Name">Identifier used in the report.</param>
    /// <param name="Center">Region centre in physical screen pixels.</param>
    /// <param name="HitRadiusPx">Hit radius in physical pixels — the censoring threshold.</param>
    /// <param name="DwellMs">Dwell time required for a completion.</param>
    public sealed record Region(string Name, PhysicalPoint Center, double HitRadiusPx, int DwellMs);

    /// <summary>What happened in one region over the stream.</summary>
    /// <param name="Region">The region's name.</param>
    /// <param name="Entries">Times gaze entered the hit radius (progress 0 → &gt; 0).</param>
    /// <param name="Completions">Times the dwell activated.</param>
    public sealed record RegionAttempts(string Region, int Entries, int Completions)
    {
        /// <summary>Completions per entry; 0 when the region was never entered.</summary>
        public double CompletionRate => Entries == 0 ? 0 : (double)Completions / Entries;

        /// <summary>
        /// The debate's screen: a rate at or below 10 % flags a bias near the hit radius. Requires at
        /// least one entry — a region nobody looked at is not evidence of anything.
        /// </summary>
        public bool FlagsBiasTowardHitRadius => Entries > 0 && CompletionRate <= 0.10;
    }

    /// <summary>The whole screen's attempt picture.</summary>
    /// <param name="Regions">One entry per region, in the order supplied.</param>
    public sealed record Report(IReadOnlyList<RegionAttempts> Regions)
    {
        /// <summary>How many regions are at or below the 10 % screen.</summary>
        public int FlaggedRegions => Regions.Count(r => r.FlagsBiasTowardHitRadius);

        /// <summary>Renders the per-region table the operator reads.</summary>
        public string Describe()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("Passive attempt log — ").Append(Regions.Count).AppendLine(" regions");
            sb.AppendLine("  region              entries  completions   rate");
            foreach (var region in Regions)
            {
                sb.Append("  ").Append(region.Region.PadRight(18))
                  .Append(region.Entries.ToString(ci).PadLeft(8))
                  .Append(region.Completions.ToString(ci).PadLeft(13))
                  .Append("  ").Append((region.CompletionRate * 100).ToString("0", ci).PadLeft(4)).AppendLine(" %");
            }
            if (FlaggedRegions > 0)
            {
                sb.Append("  ").Append(FlaggedRegions)
                  .AppendLine(" region(s) at or below 10 % — bias near the hit radius; run the probe.");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// A caller-supplied grid of screen regions — the default region set when the analyst has no
    /// specific dwell surfaces to watch. Cell centres keep every region's hit circle fully inside its
    /// cell, so adjacent regions do not steal each other's entries.
    /// </summary>
    /// <param name="screenSize">Display size in physical pixels.</param>
    /// <param name="columns">Grid columns.</param>
    /// <param name="rows">Grid rows.</param>
    /// <param name="hitRadiusPx">Hit radius each region's target gets.</param>
    /// <param name="dwellMs">Dwell time each region's target requires.</param>
    public static IReadOnlyList<Region> Grid(
        PhysicalPoint screenSize, int columns, int rows, double hitRadiusPx, int dwellMs)
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows < 1) throw new ArgumentOutOfRangeException(nameof(rows));

        var regions = new List<Region>(columns * rows);
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            regions.Add(new Region(
                $"grid_{column}_{row}",
                new PhysicalPoint(
                    screenSize.X * (column + 0.5) / columns,
                    screenSize.Y * (row + 0.5) / rows),
                hitRadiusPx,
                dwellMs));
        }
        return regions;
    }

    /// <summary>
    /// Replays the frames once per region and counts entries and completions. Gap-held frames are
    /// skipped, matching what the shipped dwell surfaces do, so this measures the behaviour the user
    /// gets rather than an idealised one. Deterministic: no clock, no device, no UI.
    /// </summary>
    /// <param name="frames">A replayed stream.</param>
    /// <param name="regions">The regions to watch.</param>
    public static Report Analyze(IReadOnlyList<ReplayPipeline.Frame> frames, IReadOnlyList<Region> regions)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(regions);

        var attempts = new List<RegionAttempts>(regions.Count);
        foreach (var region in regions)
            attempts.Add(AnalyzeRegion(frames, region));
        return new Report(attempts);
    }

    private static RegionAttempts AnalyzeRegion(IReadOnlyList<ReplayPipeline.Frame> frames, Region region)
    {
        var detector = new DwellDetector(cooldownMs: 0);
        detector.AddTarget(new DwellTarget(region.Name, region.Center, region.HitRadiusPx, region.DwellMs));

        int entries = 0, completions = 0;
        double lastProgress = 0;
        detector.TargetProgress += (_, progress) =>
        {
            // The rising edge out of zero IS the entry: the detector only reports progress while gaze
            // is inside, so the first positive value after a zero is the crossing. Counting
            // completions alone — the mutation this instrument exists to avoid — drops every attempt
            // that fell short, which is exactly the population a large bias is made of.
            if (progress > 0 && lastProgress <= 0) entries++;
            lastProgress = progress;
        };
        detector.TargetActivated += _ => completions++;

        foreach (var frame in frames)
        {
            if (frame.IsInGap) continue;
            detector.Update(frame.Filtered, frame.Raw.TimestampMicroseconds);
        }

        return new RegionAttempts(region.Name, entries, completions);
    }
}
