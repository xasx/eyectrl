namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>Projects whose UI values must come from EyeCtrl.Controls.Theme.</summary>
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
    /// </summary>
    /// <param name="text">Raw file contents.</param>
    /// <returns>The text with comments replaced by spaces.</returns>
    public static string StripComments(string text) =>
        Regex.Replace(
            Regex.Replace(
                Regex.Replace(text, "<!--.*?-->", " ", RegexOptions.Singleline),
                @"/\*.*?\*/", " ", RegexOptions.Singleline),
            @"(?<!:)//[^\n]*", " ");
}
