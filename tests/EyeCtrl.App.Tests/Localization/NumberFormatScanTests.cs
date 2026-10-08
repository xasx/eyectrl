namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// T02's format ratchet, the half CA1305 cannot see. The analyzer fires on a provider-less
/// <c>ToString()</c> and so is an error for the five UI projects (.editorconfig); it does NOT
/// inspect a <c>$"{x:F1}"</c> hole, because the interpolation compiles to a culture-bound
/// <c>DefaultInterpolatedStringHandler</c> that no analyzer argument reveals. This scan closes
/// that: an interpolated number format is allowed only inside <c>string.Create(provider, $…)</c>,
/// and the provider argument must be a surface provider
/// (<c>StringTable.FormatProviderFor("…")</c>) or <c>CultureInfo.InvariantCulture</c> — never
/// the ambient culture.
///
/// <para>
/// It also enforces the ticket's other rule: no source file sets
/// <c>CurrentCulture</c>/<c>CurrentUICulture</c>/<c>DefaultThreadCurrent*</c>/
/// <c>FrameworkElement.Language</c>. The app's language is data, not a process-culture switch.
/// </para>
/// </summary>
public class NumberFormatScanTests
{
    /// <summary>The ticket's specifier list: F/N/P/G on numbers, `g` on dates, and a custom
    /// format with a decimal point (`0.0` and siblings). `:0` alone is not matched — a rounded
    /// integer has no culture-variable glyph.</summary>
    private static readonly Regex NumberSpecifier = new(
        @"^(?:[FfNnPpGg]\d*|[0#][0#',]*\.[0#]+)$",
        RegexOptions.Compiled);

    private static readonly Regex SurfaceProvider = new(
        @"FormatProviderFor\s*\(\s*""",
        RegexOptions.Compiled);

    private static readonly Regex StringCreateCall = new(
        @"(?<![A-Za-z0-9_])string\s*\.\s*Create\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex CultureAssignment = new(
        @"(?<![A-Za-z0-9_])(?:CurrentCulture|CurrentUICulture|DefaultThreadCurrent[A-Za-z]*|Language)\s*=(?!=)",
        RegexOptions.Compiled);

    private static readonly Regex XamlLanguageAttribute = new(
        @"(?<=\s)Language\s*=",
        RegexOptions.Compiled);

    [Fact]
    public void EveryInterpolatedNumberFormat_IsBoundToAProvider()
    {
        var offenders = new List<string>();

        foreach (var (relative, text) in TextLiteralScan.ScannedSourceFiles())
        {
            if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var masked = CSharpSourceText.MaskLiterals(text);

            foreach (var literal in CSharpSourceText.FindLiterals(text))
            {
                if (!literal.Interpolated) continue;
                var formats = NumberFormats(literal.Source);
                if (formats.Count == 0) continue;
                if (IsProviderBound(text, masked, literal.Start)) continue;

                offenders.Add(
                    $"{relative}: {literal.Source} uses {string.Join(", ", formats)}; wrap it in "
                    + "string.Create(StringTable.Current.FormatProviderFor(\"<surface>\"), …)");
            }
        }

        Check.WithCustomMessage(
                "Interpolated numbers must be formatted through the UI language's provider. "
                + "CA1305 cannot see an interpolation; this scan can.\n  "
                + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }

    [Fact]
    public void NoSourceFile_SetsAProcessOrElementCulture()
    {
        var offenders = new List<string>();

        foreach (var (relative, text) in TextLiteralScan.ScannedSourceFiles())
        {
            var pattern = relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                ? XamlLanguageAttribute
                : CultureAssignment;
            foreach (Match match in pattern.Matches(text))
                offenders.Add($"{relative}: {match.Value}");
        }

        Check.WithCustomMessage(
                "The UI language is string-table data, never a process culture. Remove the "
                + "assignment; a surface's numbers follow its FormatProviderFor(…).\n  "
                + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }

    /// <summary>The number-format specifiers inside an interpolated literal's holes.</summary>
    private static List<string> NumberFormats(string source)
    {
        var openQuote = source.IndexOf('"');
        var content = source[(openQuote + 1)..^1];
        var found = new List<string>();
        var i = 0;

        while (i < content.Length)
        {
            if (content[i] == '{' && i + 1 < content.Length && content[i + 1] == '{')
            {
                i += 2;
                continue;
            }

            if (content[i] == '}')
            {
                i += i + 1 < content.Length && content[i + 1] == '}' ? 2 : 1;
                continue;
            }

            if (content[i] != '{')
            {
                i++;
                continue;
            }

            var depth = 1;
            var j = i + 1;
            while (j < content.Length && depth > 0)
            {
                if (content[j] == '{') depth++;
                else if (content[j] == '}') depth--;
                if (depth == 0) break;
                j++;
            }

            var hole = content[(i + 1)..j];
            if (FormatSpecifierOf(hole) is { } specifier && NumberSpecifier.IsMatch(specifier))
                found.Add(":" + specifier);
            i = j + 1;
        }

        return found;
    }

    /// <summary>
    /// The hole's format specifier: the text after the colon at nesting depth 0, when one is
    /// present and not a ternary's colon (<c>{cond ? "yes" : "no"}</c> has none).
    /// </summary>
    private static string? FormatSpecifierOf(string hole)
    {
        var depth = 0;
        var awaitsTernaryColon = 0;
        for (var i = 0; i < hole.Length; i++)
        {
            var c = hole[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && c == '?') awaitsTernaryColon++;
            else if (depth == 0 && c == ':' && awaitsTernaryColon == 0)
            {
                var rest = hole[(i + 1)..];
                // Alignment (`{0,5:…}`) is not used in this tree; if it appears, the comma form
                // would surface in the failure message rather than pass silently.
                return rest;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the interpolation at <paramref name="literalStart"/> is the second argument of a
    /// <c>string.Create(…)</c> call whose provider is a surface provider or the invariant culture.
    /// </summary>
    private static bool IsProviderBound(string text, string masked, int literalStart)
    {
        foreach (Match match in StringCreateCall.Matches(masked))
        {
            var openParen = match.Index + match.Length - 1;
            var args = CSharpSourceText.SplitArguments(text, openParen);
            if (args.Count < 2) continue;
            if (literalStart < args[1].Start || literalStart >= args[1].End) continue;

            var provider = text[args[0].Start..args[0].End];
            return SurfaceProvider.IsMatch(provider)
                || provider.Contains("CultureInfo.InvariantCulture", StringComparison.Ordinal);
        }

        return false;
    }

    // ────── The scanner's falsification pins ──────

    private static bool IsBound(string source) =>
        IsProviderBound(source, CSharpSourceText.MaskLiterals(source),
            CSharpSourceText.FindLiterals(source).Single(l => l.Interpolated).Start);

    [Fact]
    public void TheScan_CatchesAnUnboundInterpolatedNumber()
    {
        var literal = CSharpSourceText.FindLiterals("var s = $\"{x:F1} px\";").Single();
        Check.That(NumberFormats(literal.Source)).ContainsExactly(":F1");
        Check.That(IsBound("var s = $\"{x:F1} px\";")).IsFalse();
    }

    [Fact]
    public void TheScan_AcceptsASurfaceProvider()
    {
        var source =
            "var s = string.Create(StringTable.Current.FormatProviderFor(\"settings\"), $\"{x:F1}\");";
        var literal = CSharpSourceText.FindLiterals(source).Single(l => l.Interpolated);
        Check.That(NumberFormats(literal.Source)).ContainsExactly(":F1");
        Check.That(IsBound(source)).IsTrue();
    }

    [Fact]
    public void TheScan_AcceptsTheInvariantCultureForDiagnostics() =>
        Check.That(IsBound("var s = string.Create(CultureInfo.InvariantCulture, $\"{x:F0}\");")).IsTrue();

    [Fact]
    public void TheScan_RejectsTheAmbientCulture() =>
        Check.That(IsBound("var s = string.Create(CultureInfo.CurrentCulture, $\"{x:F1}\");")).IsFalse();

    [Fact]
    public void TheScan_IgnoresAnInterpolationWithoutANumberFormat()
    {
        var literal = CSharpSourceText.FindLiterals("var s = $\"{name} — {other}\";").Single();
        Check.That(NumberFormats(literal.Source)).IsEmpty();
    }

    [Fact]
    public void TheScan_IgnoresAPlainIntegerFormat() =>
        Check.That(NumberFormats(CSharpSourceText.FindLiterals("var s = $\"{x:0}\";").Single().Source))
            .IsEmpty();

    [Fact]
    public void TheScan_SeesOnlyInterpolatedLiterals()
    {
        // Not an interpolation: Serilog renders {X:F0} itself, and the ticket keeps logs out.
        var literals = CSharpSourceText.FindLiterals("Log.Information(\"at {X:F0}\", x);");
        Check.That(literals).HasSize(1);
        Check.That(literals[0].Interpolated).IsFalse();
    }

    [Fact]
    public void TheCultureScan_CatchesEachForbiddenAssignment()
    {
        Check.That(CultureAssignment.IsMatch("CultureInfo.CurrentCulture = de;")).IsTrue();
        Check.That(CultureAssignment.IsMatch("CultureInfo.DefaultThreadCurrentUICulture = de;")).IsTrue();
        Check.That(CultureAssignment.IsMatch("Language = de;")).IsTrue();
        Check.That(CultureAssignment.IsMatch("FrameworkElement.Language = de;")).IsTrue();
        Check.That(CultureAssignment.IsMatch("Language = ResolveLanguage(selection, culture);")).IsTrue();
    }
}
