namespace EyeCtrl.App.Tests.Overlays;

using System.Globalization;
using Xunit;

/// <summary>
/// The machine-read lines of the A/B readouts, round-tripped under de-DE (review-03 finding 1):
/// the readouts are written on a culture that uses decimal commas, and the scripts used to parse
/// them as if they used points, so "same" vs "different" compared the wrong numbers. This pins the
/// one writer/reader rule the scripts implement in PowerShell.
/// </summary>
public class BackdropReadoutLinesTests
{
    [Fact]
    public void Mean_IsWrittenAndParsedInvariantly_UnderGermanCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var line = BackdropReadoutLines.Mean(25.4, 27.5, 27.7);
            Check.WithCustomMessage($"de-DE must not leak a decimal comma into the machine line: {line}")
                .That(line).IsEqualTo("25.4;27.5;27.7");

            var parsed = BackdropReadoutLines.ParseMean(line);
            Check.That(parsed).IsNotNull();
            Check.That(parsed!.Value.R).IsCloseTo(25.4, 1e-9);
            Check.That(parsed.Value.G).IsCloseTo(27.5, 1e-9);
            Check.That(parsed.Value.B).IsCloseTo(27.7, 1e-9);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Number_IsWrittenAndParsedInvariantly_UnderGermanCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var line = BackdropReadoutLines.Number(38.95);
            Check.WithCustomMessage($"de-DE must not write a decimal comma: {line}")
                .That(line).IsEqualTo("38.95");
            Check.That(BackdropReadoutLines.ParseNumber(line)).IsCloseTo(38.95, 1e-9);

            // The sub-1 case the review called out: a comma would have read as 0.
            Check.That(BackdropReadoutLines.Number(0.42)).IsEqualTo("0.42");
            Check.That(BackdropReadoutLines.ParseNumber("0.42")).IsCloseTo(0.42, 1e-9);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ParseMean_RejectsAMalformedValue()
    {
        Check.That(BackdropReadoutLines.ParseMean("25,4,27,5,27,7")).IsNull();
        Check.That(BackdropReadoutLines.ParseMean("25.4;27.5")).IsNull();
        Check.That(BackdropReadoutLines.ParseMean("25.4;27.5;not-a-number")).IsNull();
    }
}
