namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Shared file-walking and comment-stripping for the repo-wide source guards
/// (<see cref="RawColourLiteralTests"/> and <see cref="RawGeometryLiteralTests"/>).
///
/// Extracted rather than copied: the comment stripper's behaviour is subtle — see
/// <see cref="StripComments"/> — and two independent copies would drift.
/// </summary>
internal static class SourceScanner
{
    /// <summary>
    /// Projects whose UI values must come from EyeCtrl.Controls.Theme.
    ///
    /// <c>src/EyeCtrl.Supervisor</c> is **deliberately** absent, not overlooked. Its
    /// <c>UI/</c> folder is a real WPF surface carrying roughly 22 geometry and typography
    /// literal sites, and it does reference <c>EyeCtrl.Controls</c>, so it *could* read
    /// <c>Tokens</c> — but the parent plan's Task 15 Step 5 states that
    /// "EyeCtrl.Supervisor is out of scope and is not scanned". Recorded here so a future
    /// reader does not read the omission as an oversight and widen the scan without
    /// revisiting that decision.
    /// </summary>
    public static readonly string[] ScannedProjects =
    [
        "src/EyeCtrl.App",
        "src/EyeCtrl.Controls",
    ];

    /// <summary>Walks up from the test assembly to the directory holding EyeCtrl.slnx.</summary>
    /// <returns>The repository root.</returns>
    public static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(SourceScanner).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        return dir ?? throw new InvalidOperationException("Could not locate EyeCtrl.slnx");
    }

    /// <summary>
    /// Every .cs and .xaml file in the scanned projects, excluding obj/ and bin/, as
    /// (repo-relative path with forward slashes, comment-stripped text) pairs.
    /// </summary>
    /// <returns>One entry per scanned file.</returns>
    public static IEnumerable<(string Relative, string Text)> ScannedFiles()
    {
        var root = RepoRoot().FullName;
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

                yield return (Path.GetRelativePath(root, file).Replace('\\', '/'),
                              StripComments(File.ReadAllText(file)));
            }
        }
    }

    /// <summary>
    /// Comments are not live code — a comment explaining which value was *replaced* must not
    /// fail a guard. Strips XAML comments, C# block comments, and C# line comments;
    /// <c>//</c> preceded by <c>:</c> is left alone so <c>pack://</c> and <c>http://</c>
    /// inside string literals do not truncate the rest of the line.
    ///
    /// **Line structure is preserved.** A multi-line XAML or block comment is replaced by a
    /// space *plus one newline per newline it contained*, not by a bare space. The bare-space
    /// version collapsed every such comment onto one line, which shifted the line number of
    /// everything after it — silently wrong by the height of each comment. Line numbers are a
    /// reported diagnostic in <see cref="RawGeometryLiteralTests"/>, and an off-by-N line
    /// number costs more than no line number at all. The leading space is kept so that a
    /// comment *inside* an identifier (<c>Font/*x*/Size</c>) still cannot fuse into one token.
    /// The line-comment rule needs no such treatment: <c>[^\n]*</c> never consumes the
    /// newline that ends the comment.
    /// </summary>
    /// <param name="text">Raw file contents.</param>
    /// <returns>The text with comments blanked out, keeping every line break.</returns>
    public static string StripComments(string text) =>
        Regex.Replace(
            Regex.Replace(
                Regex.Replace(text, "<!--.*?-->", BlankKeepingLines, RegexOptions.Singleline),
                @"/\*.*?\*/", BlankKeepingLines, RegexOptions.Singleline),
            @"(?<!:)//[^\n]*", " ");

    /// <summary>
    /// Replaces a stripped comment with a space followed by as many newlines as it spanned, so
    /// that every line after it keeps its original number.
    /// </summary>
    /// <param name="m">The stripped comment.</param>
    /// <returns>The line-count-preserving replacement.</returns>
    private static string BlankKeepingLines(Match m) =>
        " " + new string('\n', m.Value.Count(c => c == '\n'));
}
