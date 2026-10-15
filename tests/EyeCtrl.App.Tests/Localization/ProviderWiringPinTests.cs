namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// T02 fixup-01 F5: the provider guards test helpers and pure functions; these pins test the
/// WIRING, because dropping it is invisible. Removing the slider's <c>formatProvider:</c> looks
/// identical until the German phase switches on and the plate then shows "1.5" on a German screen;
/// reverting the Supervisor window to <c>$"{int}"</c> + <c>int.TryParse(text, out …)</c> builds
/// clean — CA1305 does not flag <c>int.TryParse(string, out int)</c> — and the round-trip tests
/// stay green because they exercise <c>SupervisorNumericField</c> only.
/// </summary>
public class ProviderWiringPinTests
{
    private static readonly Regex SliderConstruction = new(
        @"(?<![A-Za-z0-9_])new\s+(?:[A-Za-z0-9_]+\s*\.\s*)*DwellSlider\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex IntTryParse = new(
        @"(?<![A-Za-z0-9_.])int\s*\.\s*TryParse\s*\(",
        RegexOptions.Compiled);

    /// <summary>An interpolation that mentions one of the two advanced numeric fields.</summary>
    private static readonly Regex RawFieldInterpolation = new(
        @"\$""[^""]*(?:MaxRetries|HangThresholdSeconds)",
        RegexOptions.Compiled);

    /// <summary>
    /// The only accepted slider provider argument (fixup-02 S1b): the surface provider with a
    /// literal id. Presence alone would pass <c>formatProvider: CultureInfo.CurrentCulture</c>,
    /// which is exactly the ambient-culture wiring the guard exists to forbid.
    /// </summary>
    private static readonly Regex ProviderArgument = new(
        @"formatProvider\s*:\s*StringTable\s*\.\s*Current\s*\.\s*FormatProviderFor\s*\(\s*""[^""]+""\s*\)",
        RegexOptions.Compiled);

    /// <summary>Every slider construction in the files, and the ones that miss the host provider.</summary>
    private static (int Constructions, List<string> Offenders) ScanSliders(
        IEnumerable<(string Relative, string Text)> files)
    {
        var constructions = 0;
        var offenders = new List<string>();

        foreach (var (relative, text) in files)
        {
            if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var masked = CSharpSourceText.MaskLiterals(text);
            foreach (Match match in SliderConstruction.Matches(masked))
            {
                constructions++;
                var openParen = match.Index + match.Length - 1;
                var closeParen = CSharpSourceText.FindCloseParen(text, openParen);
                if (closeParen < 0) continue;
                if (!ProviderArgument.IsMatch(text[openParen..closeParen]))
                    offenders.Add(
                        $"{relative}: new DwellSlider(…) does not pass "
                        + "formatProvider: StringTable.Current.FormatProviderFor(\"…\")");
            }
        }

        return (constructions, offenders);
    }

    [Fact]
    public void EveryDwellSlider_InSource_PassesTheHostProvider()
    {
        var (constructions, offenders) = ScanSliders(TextLiteralScan.ScannedSourceFiles());

        // A floor, so a scan that stops seeing the construction (a renamed type, a changed regex)
        // cannot pass with nothing to check.
        Check.WithCustomMessage(
                "the scan found no `new DwellSlider(` in src/; the settings window builds one, so the "
                + "pattern or the scanned file set has drifted and this guard is checking nothing.")
            .That(constructions).IsStrictlyGreaterThan(0);

        Check.WithCustomMessage(
                "Every DwellSlider in src/ must take its provider from the host — a surface "
                + "provider with a literal id. Without it (or with the ambient culture) the plate "
                + "silently shows English decimals on a German surface once Step 2+ switches on.\n  "
                + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }

    [Fact]
    public void TheSliderScan_SeesAQualifiedConstructor_AndRejectsAMissingProvider()
    {
        const string provider = "formatProvider: StringTable.Current.FormatProviderFor(\"settings\")";

        var (okCount, ok) = ScanSliders(
            [("src/A.cs", $"var s = new EyeCtrl.Controls.DwellSlider(r, id, label, {provider});")]);
        Check.That(okCount).IsEqualTo(1);
        Check.That(ok).IsEmpty();

        var (count, offenders) = ScanSliders(
            [("src/B.cs", "var s = new EyeCtrl.Controls.DwellSlider(r, id, label);"),
             ("src/C.cs", "var s = new DwellSlider(r, id, label, formatProvider: CultureInfo.CurrentCulture);")]);
        Check.That(count).IsEqualTo(2);
        Check.That(offenders).HasSize(2);
    }

    [Fact]
    public void TheSupervisorSetup_ReadsAndWritesItsNumbersOnlyThroughTheHelper()
    {
        const string file = "src/EyeCtrl.Supervisor/UI/SupervisorSetupWindow.xaml.cs";
        var text = TextLiteralScan.ScannedSourceFiles()
            .Single(f => f.Relative == file)
            .Text;

        Check.WithCustomMessage("the window no longer formats its numeric fields through SupervisorNumericField.")
            .That(text.Contains("SupervisorNumericField.Format(", StringComparison.Ordinal)).IsTrue();
        Check.WithCustomMessage("the window no longer parses its numeric fields through SupervisorNumericField.")
            .That(text.Contains("SupervisorNumericField.TryParse(", StringComparison.Ordinal)).IsTrue();

        Check.WithCustomMessage(
                "the window parses an integer without the helper. CA1305 does not flag "
                + "int.TryParse(string, out int), so nothing else catches this.")
            .That(IntTryParse.IsMatch(text)).IsFalse();
        Check.WithCustomMessage(
                "the window formats one of its numeric fields with a raw interpolation, whose "
                + "separator follows the ambient culture.")
            .That(RawFieldInterpolation.IsMatch(text)).IsFalse();
    }
}
