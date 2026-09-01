namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// Source-scan guard over the one rule that actually keeps <c>DpiHelper</c>'s process-global
/// scale safe under a parallel test runner:
///
/// <para><b>Every test class that CALLS <c>DpiHelper.Initialize</c> must belong to a collection
/// declared <c>DisableParallelization = true</c>.</b></para>
///
/// <para>
/// Writers, not readers. This inverts the membership rule these collections were originally
/// documented with, and the correction is the point of this guard. xUnit v3 gives a
/// <c>DisableParallelization</c> collection an EXCLUSIVE phase — it runs concurrently with no
/// other collection, parallel or non-parallel (pinned by
/// <see cref="Fixtures.NonParallelCollectionPin"/>). So confining every writer to such a
/// collection is sufficient on its own: while a writer runs, nothing else runs, and a reader
/// therefore can never observe a scale change mid-test. Readers need no membership at all.
/// </para>
///
/// <para>
/// Why the rule was originally written the other way round. The comments added with these
/// collections stated that <c>DisableParallelization</c> "only serialises within a collection" —
/// true of xUnit v2, not of v3 — and concluded that readers had to be corralled too, which is
/// impossible for the seven reader classes already claimed by another collection, since xUnit
/// allows a class only one. Under the correct semantics those seven were never exposed. Keeping
/// the rule as prose is what let the wrong version survive; hence a test.
/// </para>
///
/// <para>
/// Scope and precision. All test projects are scanned, not just this one, because
/// <c>EyeCtrl.Core.Tests</c> and <c>EyeCtrl.ViewModels.Tests</c> hold writers too and each
/// carries its own DpiHelper collection. Collection names are resolved per project: collections
/// are per-assembly, and each test project is a separate process, so a name declared in one
/// project grants nothing in another. Matching is per FILE rather than per class — the repo's
/// other source guards work the same way — which is exact for every writer today (each lives in
/// a file whose classes share one collection) but would need tightening if someone put a writer
/// class and an unrelated uncollected class in one file.
/// </para>
///
/// <para>
/// Restoring the scale is a separate obligation this guard does NOT check: exclusivity stops a
/// writer being observed mid-flight, but a writer that leaks a non-default scale still changes
/// what later tests read, because the order between non-parallel collections is not contractual.
/// Every writer today restores 1.0 in a <c>Dispose</c> or a <c>finally</c>.
/// </para>
/// </summary>
public class DpiHelperWriterIsolationTests
{
    /// <summary>
    /// The call this guard hunts for, assembled from two halves so the needle never appears
    /// verbatim in this file. Written whole, the constant is code rather than a comment, so the
    /// comment stripper cannot remove it and the guard reports ITSELF as an uncollected writer —
    /// the same self-match trap that makes the source-census tests look like DpiHelper readers.
    /// A path-based self-exclusion would work too, but goes stale the moment the file is renamed.
    /// </summary>
    private static readonly string WriterCall = "DpiHelper" + ".Initialize(";

    /// <summary>The same call, spelled for human-readable failure messages.</summary>
    private static readonly string WriterCallDisplay = WriterCall + "...)";

    private static readonly Regex ExclusiveCollectionDefinition = new(
        @"\[\s*CollectionDefinition\s*\(\s*""(?<name>[^""]+)""\s*,\s*DisableParallelization\s*=\s*true\s*\)\s*\]",
        RegexOptions.Compiled);

    private static readonly Regex CollectionMembership = new(
        @"\[\s*Collection\s*\(\s*""(?<name>[^""]+)""\s*\)\s*\]",
        RegexOptions.Compiled);

    /// <summary>
    /// Every file calling <see cref="WriterCall"/> sits in a collection its own project declares
    /// non-parallel.
    /// </summary>
    [Fact]
    public void EveryDpiHelperWriter_LivesInANonParallelCollection()
    {
        var violations = new List<string>();
        var writersSeen = 0;

        foreach (var project in TestProjects())
        {
            var files = ProjectSources(project).ToList();

            var exclusive = files
                .SelectMany(f => ExclusiveCollectionDefinition.Matches(f.Text).Cast<Match>())
                .Select(m => m.Groups["name"].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (relative, text) in files)
            {
                if (!text.Contains(WriterCall, StringComparison.Ordinal)) continue;
                writersSeen++;

                var memberships = CollectionMembership.Matches(text).Cast<Match>()
                    .Select(m => m.Groups["name"].Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (memberships.Count == 0)
                {
                    violations.Add(
                        $"{relative}: calls {WriterCallDisplay} but declares no [Collection(...)], so it "
                        + "runs in the parallel phase where concurrent readers can observe the "
                        + "scale change.");
                    continue;
                }

                foreach (var name in memberships.Where(n => !exclusive.Contains(n)))
                    violations.Add(
                        $"{relative}: calls {WriterCallDisplay} from [Collection(\"{name}\")], which "
                        + $"{project.Name} does not declare with DisableParallelization = true.");
            }
        }

        // Guards the guard: a scan that silently matched nothing would pass vacuously forever.
        Check.WithCustomMessage(
                $"No test file calling {WriterCallDisplay} was found. Either the scan broke (wrong root, "
                + "wrong project list, comment stripper eating code) or the call was renamed — "
                + "either way this guard is no longer checking anything.")
            .That(writersSeen)
            .IsStrictlyGreaterThan(0);

        Check.WithCustomMessage(
                "A test writes DpiHelper's process-global scale from a collection that can run "
                + "concurrently with other tests. Any reader running at the same moment can then "
                + "see the scale flip mid-test — the failure mode that produced the original "
                + "parallelism flake. Fix by moving the class into a collection declared "
                + "[CollectionDefinition(..., DisableParallelization = true)] in the SAME test "
                + "project, not by widening this scan.\n  " + string.Join("\n  ", violations))
            .That(violations)
            .IsEmpty();
    }

    /// <summary>Every <c>tests/*</c> directory holding a .csproj.</summary>
    private static IEnumerable<DirectoryInfo> TestProjects()
    {
        var tests = new DirectoryInfo(Path.Combine(SourceScanner.RepoRoot().FullName, "tests"));
        return tests.EnumerateDirectories()
            .Where(d => d.EnumerateFiles("*.csproj").Any())
            .OrderBy(d => d.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// The project's .cs files as (repo-relative path, comment-stripped text). Comments are
    /// stripped so that prose naming <c>DpiHelper.Initialize</c> — this file's own summary
    /// included — is not mistaken for a call.
    /// </summary>
    private static IEnumerable<(string Relative, string Text)> ProjectSources(DirectoryInfo project)
    {
        var root = SourceScanner.RepoRoot().FullName;
        foreach (var file in project.EnumerateFiles("*.cs", SearchOption.AllDirectories))
        {
            var path = file.FullName;
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            yield return (Path.GetRelativePath(root, path).Replace('\\', '/'),
                          SourceScanner.StripComments(File.ReadAllText(path)));
        }
    }
}
