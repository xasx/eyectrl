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
        @"(?<![A-Za-z0-9_])new\s+DwellSlider\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex IntTryParse = new(
        @"(?<![A-Za-z0-9_.])int\s*\.\s*TryParse\s*\(",
        RegexOptions.Compiled);

    /// <summary>An interpolation that mentions one of the two advanced numeric fields.</summary>
    private static readonly Regex RawFieldInterpolation = new(
        @"\$""[^""]*(?:MaxRetries|HangThresholdSeconds)",
        RegexOptions.Compiled);

    [Fact]
    public void EveryDwellSlider_InSource_PassesTheHostProvider()
    {
        var offenders = new List<string>();

        foreach (var (relative, text) in TextLiteralScan.ScannedSourceFiles())
        {
            if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var masked = CSharpSourceText.MaskLiterals(text);
            foreach (Match match in SliderConstruction.Matches(masked))
            {
                var openParen = match.Index + match.Length - 1;
                var closeParen = CSharpSourceText.FindCloseParen(text, openParen);
                if (closeParen < 0) continue;
                if (!text[openParen..closeParen].Contains("formatProvider:", StringComparison.Ordinal))
                    offenders.Add($"{relative}: new DwellSlider(…) does not pass formatProvider:");
            }
        }

        Check.WithCustomMessage(
                "Every DwellSlider in src/ must take its provider from the host. Without it the "
                + "control silently falls back to invariant, and the plate shows English decimals "
                + "on a German surface once Step 2+ switches on.\n  " + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
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
