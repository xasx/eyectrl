namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.IO;
using NFluent;
using Xunit;

/// <summary>
/// Pins where the two note render suites write: under the user's temp folder, never inside the
/// repository. Before this, every unfiltered App run left <c>note-reading-renders/</c> and
/// <c>note-badge-renders/</c> in whichever worktree ran it — visible as untracked files there,
/// because only the main checkout's local <c>.git/info/exclude</c> hid them.
/// <para>
/// The pin calls the same helpers the renderers use, so restoring a repo-root default in either
/// one turns it RED. It is window-free: it resolves paths only — no WPF, no window, and it
/// creates no file. The override is READ, never set; where the machine sets it, the render
/// folders follow the operator's choice rather than the default and the pin skips.
/// </para>
/// </summary>
public class NoteRenderDirectoriesPinTests
{
    [Fact]
    public void BothRenderDirectories_LiveUnderTemp_AndNotUnderTheRepoRoot()
    {
        if (Environment.GetEnvironmentVariable("EYECTRL_READING_RENDER_DIR") is { Length: > 0 })
        {
            Assert.Skip(
                "EYECTRL_READING_RENDER_DIR is set on this machine: the render folders follow the "
                + "override, so they are the operator's choice and the default is not in play.");
        }

        var temp = Path.GetTempPath();
        var repoRoot = SourceScanner.RepoRoot().FullName;
        foreach (var (name, directory) in new[]
        {
            ("reading", NoteReadingRenderTests.RenderDirectory()),
            ("badge", NoteBadgeRenderTests.RenderDirectory()),
        })
        {
            Check.WithCustomMessage(
                    $"{name} renders must land under the temp folder ({temp}), but resolve to {directory}")
                .That(IsUnder(directory, temp)).IsTrue();
            Check.WithCustomMessage(
                    $"{name} renders must not land in the repo root ({repoRoot}), but resolve to {directory}")
                .That(IsUnder(directory, repoRoot)).IsFalse();
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or sits inside it,
    /// compared case-insensitively (Windows paths) and without a sibling-prefix false match.</summary>
    private static bool IsUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
