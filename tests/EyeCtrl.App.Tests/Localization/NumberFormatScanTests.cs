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
/// that: an interpolated culture-variable format is allowed only inside
/// <c>string.Create(provider, $…)</c>, where the provider is
/// <c>StringTable.FormatProviderFor("…")</c> — never the ambient culture — or, on a short,
/// reasoned allow-list, <c>CultureInfo.InvariantCulture</c> for diagnostics that never reach the
/// screen.
///
/// <para>
/// <b>Accepted residuals (recorded, not gated).</b> Three forms need type information a regex
/// scan does not have, so they are recorded here rather than gated: <c>$"{d}"</c> on a double,
/// <c>"x" + d</c>, and <c>StringBuilder.Append(d)</c>. Two things make that safe: T06–T08 route
/// on-screen text through <c>T(key, args)</c>, which formats every argument with the surface's
/// provider, and the literal ratchet's end state forbids a bare UI literal of any shape. T09 may
/// add a Roslyn semantic gate over the same surface and retire this paragraph.
/// </para>
///
/// <para>
/// It also enforces the ticket's other rule: no source file sets
/// <c>CurrentCulture</c>/<c>CurrentUICulture</c>/<c>DefaultThreadCurrent*</c>/
/// <c>FrameworkElement.Language</c>. The app's language is data, not a process-culture switch.
/// </para>
/// </summary>
public class NumberFormatScanTests
{
    /// <summary>
    /// Standard numeric (C, D, E, F, G, N, P, R, X) and standard date (d, D, f, F, g, G, M, t, T,
    /// Y) specifiers, each with an optional precision.
    /// </summary>
    private static readonly Regex StandardSpecifier = new(
        @"^(?:[CcDdEeFfGgNnPpRrXx]|[dDfFgGMtTY])\d{0,3}$",
        RegexOptions.Compiled);

    /// <summary>A custom date/time pattern letter — makes a specifier culture-variable.</summary>
    private const string DatePatternLetters = "yMdHhmsftzK";

    /// <summary>Custom-number markers: a scaling, decimal or group separator in the format.</summary>
    private const string CustomNumberMarkers = "0#,%\u2030";

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

    /// <summary>
    /// The diagnostics allowed to bind <c>CultureInfo.InvariantCulture</c> instead of a surface
    /// provider, each with its one-line reason. Every entry must match exactly one interpolated
    /// literal in its file and must still be that call's provider argument —
    /// <see cref="TheInvariantAllowList_EntriesStillExistAndAreBound"/> fails when one rots, so the
    /// list cannot grow or linger silently. A UI site can never appear here: it is never a
    /// diagnostic.
    /// </summary>
    internal static readonly (string File, string Literal, string Reason)[] InvariantDiagnostics =
    [
        ("src/EyeCtrl.App/Services/ToolbarAutoHideController.cs",
            "$\"raw gaze inside the summon margin band for 100+ ms (raw y={sample.Y:F0}, x={sample.X:F0}, margin={_offScreenMarginPx:F0}) — short of the summon threshold\"",
            "a Diag() line — diagnostics, never screen text"),
        ("src/EyeCtrl.App/Services/ToolbarAutoHideController.cs",
            "$\"summon run started (raw y={sample.Y:F0}, x={sample.X:F0}, margin={_offScreenMarginPx:F0})\"",
            "a Diag() line — diagnostics, never screen text"),
        ("src/EyeCtrl.App/Services/ToolbarAutoHideController.cs",
            "$\"summon run reset after {(timestampUs - runStart) / 1000} ms (raw y={sample.Y:F0}, x={sample.X:F0})\"",
            "a Diag() line — diagnostics, never screen text"),
        ("src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs",
            "$\"[{z.X:F0},{z.Y:F0} {z.Width:F0}×{z.Height:F0}]\"",
            "a fragment of the Activation-chrome Serilog line — never screen text"),
        // The magnifier's shown line: machine-read field evidence (the A4 session check), never
        // screen text. A German decimal comma here would break an invariant parse of mag=1.5.
        ("src/EyeCtrl.App/Overlays/ManualSelectionOverlayWindow.xaml.cs",
            "$\"Magnifier shown: P=({captureCenter.X:F0},{captureCenter.Y:F0}) view=({viewCenter.X:F0},{viewCenter.Y:F0}) mag={_magnification:0.##} viewDip={radius * 2 * _magnification:F0} circleEff={radius} recapture={recapture} picture={_pictureState} captureMs={_lastCaptureMs} captured={_lastCapturedSize}x{_lastCapturedSize}px luma={_lastLumaMean:F0}\\u00B1{_lastLumaSd:F0} sameAsPrevious={_lastCaptureSameAsPrevious}\"",
            "the magnifier's shown Serilog line — never screen text, read by the field-log check"),
        // T0(a): the two LAG MARK log lines are machine-read by the session readout, whose format
        // is pinned invariant (spec §2.3) — a German decimal comma would break its parse.
        ("src/EyeCtrl.App/Services/LagMarkHost.cs",
            "$\"Lag mark button shown at {rect.Left:0},{rect.Top:0} {rect.Width:0}×{rect.Height:0} DIP (scale {DpiHelper.ScaleX:0.##})\"",
            "the LagMark button-shown Serilog line — never screen text, parsed by the launcher"),
        ("src/EyeCtrl.App/Services/LagMarkHost.cs",
            "$\"LAG MARK #{MarkCount} at {at:yyyy-MM-dd HH:mm:ss.fff} — last 15 s: {DispatchLatencyProbe.DescribeWindow(summary)}\"",
            "the LAG MARK Serilog line — never screen text, parsed by the session readout"),
    ];

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
                if (IsProviderBound(relative, text, masked, literal)) continue;

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

    /// <summary>
    /// Whether a specifier renders culture-variable output: a standard numeric or date specifier,
    /// a custom numeric format (any <c>0 # , % ‰</c>), or a custom date pattern (any pattern
    /// letter). <c>{x}</c> with no specifier is not matched — that form needs type information and
    /// is an accepted residual (see the class remarks).
    /// </summary>
    private static bool IsCultureSensitiveSpecifier(string specifier)
    {
        if (specifier.Length == 0) return false;
        return StandardSpecifier.IsMatch(specifier)
            || specifier.Any(c => CustomNumberMarkers.Contains(c))
            || specifier.Any(c => DatePatternLetters.Contains(c));
    }

    /// <summary>The culture-variable format specifiers inside an interpolated literal's holes.</summary>
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
            if (FormatSpecifierOf(hole) is { } specifier && IsCultureSensitiveSpecifier(specifier))
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
    /// True when the interpolation is the second argument of a <c>string.Create(…)</c> call whose
    /// provider is a surface provider, or the invariant culture at an allow-listed diagnostics
    /// site (<see cref="InvariantDiagnostics"/>).
    /// </summary>
    private static bool IsProviderBound(
        string file, string text, string masked, CSharpSourceText.Literal literal)
    {
        foreach (Match match in StringCreateCall.Matches(masked))
        {
            var openParen = match.Index + match.Length - 1;
            var args = CSharpSourceText.SplitArguments(text, openParen);
            if (args.Count < 2) continue;
            if (literal.Start < args[1].Start || literal.Start >= args[1].End) continue;

            var provider = text[args[0].Start..args[0].End];
            if (SurfaceProvider.IsMatch(provider)) return true;
            if (provider.Contains("CultureInfo.InvariantCulture", StringComparison.Ordinal))
            {
                return InvariantDiagnostics.Any(
                    entry => entry.File == file && entry.Literal == literal.Source);
            }

            return false;
        }

        return false;
    }

    // ────── The scanner's falsification pins ──────

    private const string FixtureFile = "src/EyeCtrl.App/Views/Fixture.cs";

    private static bool IsBound(string source)
    {
        var literal = CSharpSourceText.FindLiterals(source).Single(l => l.Interpolated);
        return IsProviderBound(FixtureFile, source, CSharpSourceText.MaskLiterals(source), literal);
    }

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

    /// <summary>
    /// The review's F4 probes: each must be recognised as culture-variable now. The de-DE values
    /// they render are in the review's table; the gate's job is to see them at all.
    /// </summary>
    [Theory]
    [InlineData("var s = $\"{d:#,##0}\";", ":#,##0")]
    [InlineData("var s = $\"{d:0.0%}\";", ":0.0%")]
    [InlineData("var s = $\"{d:E2}\";", ":E2")]
    [InlineData("var s = $\"{d:D2}\";", ":D2")]
    [InlineData("var s = $\"{d:X}\";", ":X")]
    [InlineData("var s = $\"{date:d}\";", ":d")]
    [InlineData("var s = $\"{date:yyyy-MM-dd}\";", ":yyyy-MM-dd")]
    public void TheScan_CatchesTheWidenedSpecifierForms(string source, string expected)
    {
        var literal = CSharpSourceText.FindLiterals(source).Single();
        Check.That(NumberFormats(literal.Source)).Contains(expected);
        Check.That(IsBound(source)).IsFalse();
    }

    [Fact]
    public void TheScan_RejectsInvariantAtAnUnlistedSite() =>
        Check.That(IsBound("var s = string.Create(CultureInfo.InvariantCulture, $\"{x:F0}\");")).IsFalse();

    [Fact]
    public void TheScan_RejectsTheAmbientCulture() =>
        Check.That(IsBound("var s = string.Create(CultureInfo.CurrentCulture, $\"{x:F1}\");")).IsFalse();

    /// <summary>
    /// Anti-rot for the one exception: every allow-listed literal is still in its file and still
    /// the provider argument of its <c>string.Create</c> call. Without this, an allow-list entry
    /// could keep a licence alive for text that moved.
    /// </summary>
    [Fact]
    public void TheInvariantAllowList_EntriesStillExistAndAreBound()
    {
        var files = TextLiteralScan.ScannedSourceFiles().ToDictionary(f => f.Relative, f => f.Text);

        foreach (var (file, literalSource, reason) in InvariantDiagnostics)
        {
            Check.WithCustomMessage($"the allow-list names {file}, which is not scanned.")
                .That(files.ContainsKey(file)).IsTrue();
            var text = files[file];
            var literal = CSharpSourceText.FindLiterals(text)
                .SingleOrDefault(l => l.Source == literalSource);
            Check.WithCustomMessage(
                    $"the allow-list entry '{literalSource}' ({reason}) is no longer in {file}; drop it.")
                .That(literal).IsNotNull();
            Check.WithCustomMessage(
                    $"'{literalSource}' is no longer invariant-bound in {file}; the allow-list entry "
                    + "must go, or the diagnostics site must stop formatting.")
                .That(IsProviderBound(file, text, CSharpSourceText.MaskLiterals(text), literal!)).IsTrue();
        }
    }

    [Fact]
    public void TheScan_IgnoresAnInterpolationWithoutANumberFormat()
    {
        var literal = CSharpSourceText.FindLiterals("var s = $\"{name} — {other}\";").Single();
        Check.That(NumberFormats(literal.Source)).IsEmpty();
    }

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
