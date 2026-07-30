namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// Repo-wide guard: no raw colour may appear in UI source outside the palette.
///
/// This exists because every other guard is keyed on the wrong thing.
/// <c>OverlayInventoryDriftTests</c> enumerates <c>OverlayWindow</c> subclasses, so a class
/// that *builds* overlay content without being a window is invisible to it — that is how
/// <c>EmojiGridManager</c> kept six pre-migration colour constants and two
/// <c>Brushes.White</c> foregrounds through the whole migration.
/// <c>LegacyColourLiteralTests</c> only scans for the outgoing Lime accent.
/// <c>OverlayResourceResolutionTests</c> catches misspelled keys, not literals.
///
/// So this test scans *files*, not types, and requires every exception to be listed with a
/// reason. A new raw colour anywhere in the UI projects fails the build until it is either
/// migrated onto a token or explicitly justified here.
/// </summary>
public class RawColourLiteralTests
{
    /// <summary>Projects whose colours must come from <c>EyeCtrl.Controls.Theme.Tokens</c>.</summary>
    private static readonly string[] ScannedProjects =
    [
        "src/EyeCtrl.App",
        "src/EyeCtrl.Controls",
    ];

    /// <summary>
    /// Files allowed to contain raw colours, each with the reason. Keep this list short and
    /// keep the reasons specific — "it is fine" is not a reason. A path listed here is
    /// exempt in full, so prefer migrating over listing.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/EyeCtrl.Controls/Theme/Tokens.cs"] =
            "the palette itself — the one place a colour literal is correct",
        ["src/EyeCtrl.Controls/DwellColorPicker.cs"] =
            "DefaultPresets are user-selectable palette values, gated by ColorPickerPresetTests",
    };

    /// <summary>
    /// Raw colour forms: a hex literal in XAML or C#, a named System.Windows.Media brush or
    /// XAML colour keyword, or a Color.FromRgb/FromArgb built from numeric literals — the form
    /// EmojiGridManager used, and the one a hex-only scan misses. Transparent is excluded: it
    /// is a structural value, not a colour, and every overlay legitimately uses it.
    /// </summary>
    private static readonly Regex RawColour = new(
        """
        \#[0-9A-Fa-f]{3,8}\b
        | Brushes\.(?!Transparent\b)[A-Z][A-Za-z]+
        | Colors\.(?!Transparent\b)[A-Z][A-Za-z]+
        | Color\.From A? rgb \s* \(\s* (?: 0x[0-9A-Fa-f]+ | [0-9]+ ) \s* ,
        | (?:Stroke|Fill|Foreground|Background|BorderBrush)\s*=\s*"(?!Transparent\b|\{)[A-Za-z]+"
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    /// <summary>
    /// Comments are not live code — a comment explaining which colour was *replaced* must not
    /// fail the guard. Strips XAML comments, C# block comments, and C# line comments; <c>//</c>
    /// preceded by <c>:</c> is left alone so <c>pack://</c> and <c>http://</c> inside string
    /// literals do not truncate the rest of the line.
    /// </summary>
    private static string StripComments(string text) =>
        Regex.Replace(
            Regex.Replace(
                Regex.Replace(text, "<!--.*?-->", " ", RegexOptions.Singleline),
                @"/\*.*?\*/", " ", RegexOptions.Singleline),
            @"(?<!:)//[^\n]*", " ");

    /// <summary>
    /// <c>Brushes.</c> and <c>Colors.</c> also match field names like <c>_keyBrushes.Clear()</c>
    /// or <c>_predictionBrushes.Count</c>. Require the match to start at a type-name boundary,
    /// i.e. not be preceded by an identifier character, <c>_</c> or <c>.</c>.
    /// </summary>
    private static bool IsFieldAccess(string text, Match m)
    {
        if (m.Index == 0) return false;
        var prev = text[m.Index - 1];
        return char.IsLetterOrDigit(prev) || prev is '_' or '.';
    }

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(RawColourLiteralTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return dir!;
    }

    [Fact]
    public void NoUIFile_ContainsARawColourOutsideThePalette()
    {
        var root = RepoRoot().FullName;
        var offenders = new List<string>();

        foreach (var project in ScannedProjects)
        {
            var dir = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (Exempt.ContainsKey(relative)) continue;

                var text = StripComments(File.ReadAllText(file));
                var found = RawColour.Matches(text)
                    .Where(m => !IsFieldAccess(text, m))
                    .Select(m => m.Value)
                    .Distinct()
                    .ToList();

                if (found.Count > 0)
                    offenders.Add($"{relative}: {string.Join(", ", found)}");
            }
        }

        Check.WithCustomMessage(
                "Raw colours found outside the palette. Migrate them onto "
                + "EyeCtrl.Controls.Theme.Tokens, or add the file to Exempt with a specific "
                + "reason:\n  " + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }

    [Fact]
    public void EveryExemption_NamesAFileThatExistsAndActuallyHasARawColour()
    {
        // Stops the allow-list rotting: an exemption for a file that no longer has a raw
        // colour is silently protecting nothing, and would hide a future one.
        var root = RepoRoot().FullName;
        var stale = new List<string>();

        foreach (var (relative, _) in Exempt)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                stale.Add($"{relative}: file does not exist");
                continue;
            }

            var text = StripComments(File.ReadAllText(path));
            var any = RawColour.Matches(text).Any(m => !IsFieldAccess(text, m));
            if (!any)
                stale.Add($"{relative}: no raw colour left — drop the exemption");
        }

        Check.WithCustomMessage("Stale exemptions:\n  " + string.Join("\n  ", stale))
            .That(stale).IsEmpty();
    }
}
