namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Cross-language drift guard. tools/palette_validate.py holds each theme's token
/// set and runs ten accessibility gates against it; these classes hold the same
/// values for WPF. If they diverge, the app ships colours nothing ever validated.
///
/// The Python file is the source of truth because it is what the gates execute
/// against — so this test parses it rather than restating the hexes a third time.
/// </summary>
public class ThemeParityTests
{
    private static string ValidatorSource()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(ThemeParityTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.WithCustomMessage("Could not locate the repo root (EyeCtrl.slnx)")
            .That(dir).IsNotNull();
        return File.ReadAllText(Path.Combine(dir!.FullName, "tools", "palette_validate.py"));
    }

    /// <summary>
    /// Extracts one theme's token map from the THEMES dict. Keys starting with '_'
    /// are validator-internal (e.g. "_core", the opaque surface core) and skipped.
    ///
    /// Format coupling: this assumes the validator declares each theme as
    /// <c>"Name": { ... }</c> with only string-valued keys inside (every token is a
    /// quoted hex or the quoted <c>BorderThicknessStandard</c> number). It is a
    /// checked copy, not a parser — if <c>palette_validate.py</c> ever gains nested
    /// dicts, list values, or trailing commas under a theme, widen this and re-run.
    /// Brace-matching (not a regex for the closing brace) keeps it immune to
    /// indentation changes, which the previous <c>\n    }</c> form was not.
    /// </summary>
    private static Dictionary<string, string> ParseTheme(string source, string themeName)
    {
        var start = Regex.Match(source, $"\"{Regex.Escape(themeName)}\"\\s*:\\s*\\{{");
        Check.WithCustomMessage($"THEMES has no entry named '{themeName}'")
            .That(start.Success).IsTrue();

        // Walk braces from the opening { to its matching close, so indentation or
        // a reformatted file cannot truncate the block.
        var depth = 0;
        var i = start.Index + start.Length - 1;   // position of the opening '{'
        var end = -1;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
            i++;
        }
        Check.WithCustomMessage($"Could not find the end of the '{themeName}' block")
            .That(end).IsStrictlyGreaterThan(0);

        var body = source[(start.Index + start.Length)..end];
        return Regex.Matches(body, @"""(\w+)""\s*:\s*""([^""]+)""")
            .Where(m => !m.Groups[1].Value.StartsWith('_'))
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Normalises the validator's #RRGGBB form to the #AARRGGBB the WPF side produces.</summary>
    private static string Normalise(string hex) =>
        hex.Length == 7 ? "#FF" + hex[1..].ToUpperInvariant() : hex.ToUpperInvariant();

    public static TheoryData<string> ThemeNames() => new() { "Midnight", "Ember", "Contrast" };

    /// <summary>
    /// Name to theme, local to this file.
    ///
    /// Deliberately **not** <c>ThemeRegistry.Resolve</c>: the registry does not
    /// exist until Task 4, and it is itself built from these same three instances,
    /// so depending on it here would make Tasks 3 and 4 mutually dependent —
    /// neither could compile first. Task 4 Step 4 swaps this for the registry once
    /// there is one, which is also what proves the registry agrees with this list.
    /// </summary>
    private static ITheme ThemeNamed(string name) => name switch
    {
        "Midnight" => MidnightTheme.Instance,
        "Ember" => EmberTheme.Instance,
        "Contrast" => ContrastTheme.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown theme"),
    };

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void EveryColour_MatchesTheValidator(string themeName)
    {
        var expected = ParseTheme(ValidatorSource(), themeName);
        var theme = ThemeNamed(themeName);

        foreach (var property in typeof(ITheme).GetProperties()
                     .Where(p => p.PropertyType == typeof(Color)))
        {
            Check.WithCustomMessage(
                    $"tools/palette_validate.py has no '{property.Name}' for {themeName}. "
                    + "Every ITheme colour must be gated.")
                .That(expected.ContainsKey(property.Name)).IsTrue();

            var actual = Hex((Color)property.GetValue(theme)!);
            Check.WithCustomMessage(
                    $"{themeName}.{property.Name} is {actual} in C# but "
                    + $"{expected[property.Name]} in tools/palette_validate.py. "
                    + "Fix the C# to match, or change the validator and re-run it "
                    + "(python tools/palette_validate.py must exit 0) before changing this.")
                .That(actual).IsEqualTo(Normalise(expected[property.Name]));
        }
    }

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void BorderThickness_MatchesTheValidator(string themeName)
    {
        var expected = ParseTheme(ValidatorSource(), themeName);
        var theme = ThemeNamed(themeName);

        Check.WithCustomMessage(
                $"{themeName} must declare BorderThicknessStandard in the validator — "
                + "it is the one non-colour theme-varying token (§4.4).")
            .That(expected.ContainsKey("BorderThicknessStandard")).IsTrue();

        Check.That(theme.BorderThicknessStandard)
             .IsEqualTo(double.Parse(expected["BorderThicknessStandard"]));
    }

    [Fact]
    public void ValidatorAndCSharp_DeclareTheSameThemes()
    {
        // A theme gated in Python but missing from C# is unreachable; a theme in C#
        // but not in Python has never passed a single gate.
        var inPython = Regex.Matches(ValidatorSource(), @"^    ""(\w+)"":\s*\{",
                RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .OrderBy(n => n)
            .ToList();

        var inCSharp = ThemeNames().Select(row => (string)row).OrderBy(n => n).ToList();

        Check.That(inCSharp).ContainsExactly(inPython);
    }
}
