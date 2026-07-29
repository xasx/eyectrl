namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// Regression guard for the colour migration (spec §5). During the migration each
/// legacy literal was listed with the file that still contained it; as a file was
/// migrated its entry was pruned. With the migration complete <see cref="StillPending"/>
/// is empty, so <see cref="MigratedFiles_ContainNoLegacyColourLiterals"/> is now a
/// vacuous-when-empty guard that fails only if a stale entry is re-added, and
/// <see cref="NoUnlistedFile_UsesTheOutgoingLimeAccent"/> scans all of src/ for any
/// returning Lime accent that is not the unrelated Brushes.LimeGreen track-quality dot.
/// </summary>
public class LegacyColourLiteralTests
{
    /// <summary>
    /// file (repo-relative) -&gt; literals still expected in it. Empty now that every
    /// tracked file is migrated; kept (rather than deleted) so re-introducing a literal
    /// and re-listing it here is caught, and so the prune-as-you-go structure survives.
    /// </summary>
    private static readonly Dictionary<string, string[]> StillPending = new()
    {
    };

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(LegacyColourLiteralTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return dir!;
    }

    [Fact]
    public void MigratedFiles_ContainNoLegacyColourLiterals()
    {
        var root = RepoRoot().FullName;
        var offenders = new List<string>();

        foreach (var (relative, allowed) in StillPending)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue;   // file renamed/removed — not this test's business
            var text = File.ReadAllText(path);

            foreach (var literal in allowed)
                if (!text.Contains(literal, StringComparison.Ordinal))
                    offenders.Add($"{relative}: '{literal}' is gone — delete it from StillPending");
        }

        // Fails when a file has been migrated but its allow-list entry was not
        // pruned, which would let the literal silently return later.
        Check.WithCustomMessage(
                "The allow-list is stale. Remove migrated entries so the guard "
                + "starts protecting them:\n  " + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }

    /// <summary>
    /// Matches the outgoing Lime accent but NOT Brushes.LimeGreen, a different colour
    /// (#32CD32) used legitimately by TrackStatusWindow for track-quality dots. A naive
    /// Contains("Brushes.Lime") substring test flags it as a false positive; the \b
    /// prevents that, because e and G are both word characters.
    /// </summary>
    private static readonly Regex LimeAccent =
        new(@"Brushes\.Lime\b|""Lime""", RegexOptions.Compiled);

    [Fact]
    public void NoUnlistedFile_UsesTheOutgoingLimeAccent()
    {
        var root = RepoRoot().FullName;
        var listed = StillPending.Keys.Select(k => Path.Combine(root, k).Replace('/', Path.DirectorySeparatorChar))
                                      .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !listed.Contains(f))
            .Where(f => LimeAccent.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Check.WithCustomMessage(
                "Lime appeared in a file that is not on the migration allow-list: "
                + string.Join(", ", offenders))
            .That(offenders).IsEmpty();
    }
}
