namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Globalization;

/// <summary>
/// The machine-read lines of the backdrop A/B readouts (review-03 finding 1). This machine's
/// culture is de-DE, so a line formatted in the current culture writes "25,4,27,5,27,7" and every
/// script that split on ',' read (25, 4, 27) — and a stddev of "38,95" read as 38. The writer and
/// the parsers therefore share one rule: invariant culture, ';' separators, and a round-trip test
/// (<see cref="BackdropReadoutLinesTests"/>) that formats under de-DE and parses back.
/// </summary>
internal static class BackdropReadoutLines
{
    /// <summary>Three channel means, invariant, ';'-separated: <c>32.1;32.3;33.0</c>.</summary>
    internal static string Mean(double meanR, double meanG, double meanB) =>
        string.Join(
            ';',
            meanR.ToString("0.0", CultureInfo.InvariantCulture),
            meanG.ToString("0.0", CultureInfo.InvariantCulture),
            meanB.ToString("0.0", CultureInfo.InvariantCulture));

    /// <summary>One figure, invariant: <c>0.42</c>.</summary>
    internal static string Number(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture);

    /// <summary>Parses <see cref="Mean"/> back to its three doubles, or null when malformed.</summary>
    internal static (double R, double G, double B)? ParseMean(string text)
    {
        var parts = text.Split(';');
        if (parts.Length != 3) return null;
        if (!TryParse(parts[0], out var r) || !TryParse(parts[1], out var g) || !TryParse(parts[2], out var b))
            return null;
        return (r, g, b);
    }

    /// <summary>Parses <see cref="Number"/>. Throws on a malformed value, like <c>double.Parse</c>.</summary>
    internal static double ParseNumber(string text) =>
        double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
