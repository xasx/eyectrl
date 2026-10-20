namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// Source-scan guard over the rule <see cref="Fixtures.WpfApplicationGate"/> documents: every test
/// file that calls the gate's application creator must belong to a collection whose definition in
/// the SAME test project sets <c>DisableParallelization = true</c>.
///
/// <para>
/// Why the rule exists. The gate's <c>lock</c> closes only the race on CREATING the singleton. What
/// it cannot fix is what happens after: xUnit.StaFact spins a fresh STA thread per test, so a
/// class that creates the <c>Application</c> during the parallel phase can bind it to a thread that
/// later exits, or race a window construction against another class's creator. Confining every
/// creator to an exclusive collection is what the gate asks of its callers.
/// </para>
///
/// <para>
/// Why a scan rather than another collection comment. <c>AppIconTests</c> and
/// <c>ActionMenuWindowDimRenderTests</c> called the gate with no collection at all, and nothing in
/// the suite could see it — the same failure shape the DpiHelper and StringTable guards were added
/// for. This file is the third of those scans and follows their conventions: per FILE (the repo's
/// other source guards work the same way), comment-stripped, with the reported needle assembled
/// from two halves so this file does not match itself.
/// </para>
///
/// <para>
/// What the scan deliberately does not classify. Files with no test attribute of their own — the
/// gate itself, the shared helpers (<c>ToolbarTestHelpers</c>, <c>AppFitMeasurers</c>,
/// <c>ProbeRingOnScreenHarness</c>, <c>ThemeScope</c>, <c>PreviewRowFit</c>) — carry no collection
/// they could join; they run under whichever test class invokes them. Their callers are scanned
/// directly because each also names the gate, or is a test class collected with one that does.
/// A new helper callable ONLY from an uncollected class would therefore not be attributed here —
/// that is the residual this paragraph records, and the reason the rule is stated as
/// "every test file that CALLS it" rather than "every file that reaches it".
/// </para>
/// </summary>
public class WpfApplicationCreatorIsolationTests
{
    /// <summary>
    /// The call this guard hunts for, assembled from two halves so the needle never appears
    /// verbatim in this file. Written whole, it would sit in this file's own code, this file
    /// declares tests, and the scan would report ITSELF as an uncollected creator — the same
    /// self-match trap <c>DpiHelperWriterIsolationTests</c> documents.
    /// </summary>
    private static readonly string CreatorCall = "EnsureApplication" + "WithTokens(";

    /// <summary>Any attribute that makes a file a test file: <c>[Fact]</c>, <c>[StaFact]</c>,
    /// <c>[Theory]</c> or <c>[StaTheory]</c>.</summary>
    private static readonly Regex TestDeclaration = new(
        @"\[\s*(?:Sta)?(?:Fact|Theory)\b",
        RegexOptions.Compiled);

    private static readonly Regex ExclusiveCollectionDefinition = new(
        @"\[\s*CollectionDefinition\s*\(\s*""(?<name>[^""]+)""\s*,\s*DisableParallelization\s*=\s*true\s*\)\s*\]",
        RegexOptions.Compiled);

    private static readonly Regex CollectionMembership = new(
        @"\[\s*Collection\s*\(\s*""(?<name>[^""]+)""\s*\)\s*\]",
        RegexOptions.Compiled);

    [Fact]
    public void EveryTestFileCreatingTheApplication_LivesInAnExclusiveCollection()
    {
        var violations = new List<string>();
        var creatorsSeen = 0;

        foreach (var project in TestProjects())
        {
            var files = ProjectSources(project).ToList();

            var exclusive = files
                .SelectMany(f => ExclusiveCollectionDefinition.Matches(f.Text).Cast<Match>())
                .Select(m => m.Groups["name"].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (relative, text) in files)
            {
                if (!text.Contains(CreatorCall, StringComparison.Ordinal)) continue;

                // A helper file has no collection of its own; its caller's collection governs.
                if (!TestDeclaration.IsMatch(text)) continue;

                creatorsSeen++;

                var memberships = CollectionMembership.Matches(text).Cast<Match>()
                    .Select(m => m.Groups["name"].Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (memberships.Count == 0)
                {
                    violations.Add(
                        $"{relative}: calls {CreatorCall} but declares no [Collection(...)], so it "
                        + "creates the process-wide WPF Application during the parallel phase.");
                    continue;
                }

                foreach (var name in memberships.Where(n => !exclusive.Contains(n)))
                    violations.Add(
                        $"{relative}: calls {CreatorCall} from [Collection(\"{name}\")], which "
                        + $"{project.Name} does not declare with DisableParallelization = true.");
            }
        }

        // Guards the guard: a scan that silently matched nothing would pass vacuously forever.
        Check.WithCustomMessage(
                $"No test file calling {CreatorCall} was found. Either the scan broke (wrong root, "
                + "wrong project list, comment stripper eating code) or the call was renamed — "
                + "either way this guard is no longer checking anything.")
            .That(creatorsSeen)
            .IsStrictlyGreaterThan(0);

        Check.WithCustomMessage(
                "A test file creates WPF's process-wide Application from the parallel phase. Two "
                + "classes that do so can race each other's window construction, and StaFact's "
                + "per-test STA thread can leave Application.Current bound to a thread that has "
                + "exited. Fix by adding [Collection(\"WpfApplication\")] to the test class — the "
                + "collection is declared DisableParallelization = true — not by widening this "
                + "scan.\n  " + string.Join("\n  ", violations))
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
    /// stripped so that prose naming the gate — this file's own summary included — is not mistaken
    /// for a call.
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
