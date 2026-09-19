namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EyeCtrl.Core.Services;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;
using Xunit.v3;

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
/// Restoring the scale is a separate obligation, and it is NOT met by per-class discipline. The
/// sentence here used to read "Every writer today restores 1.0 in a <c>Dispose</c> or a
/// <c>finally</c>" — false when written: five tests in <c>DisplayGeometryWatcherTests</c> drove
/// production code that writes the global and left it at 2.0, and the one test that restored did
/// so as a bare trailing statement an assertion failure skips. What closes that hazard is an
/// assembly-wide per-test hook (<c>DpiHelperRestore</c>, linked from <c>tests/TestSupport</c> and
/// declared by every test project that holds a writer). It cannot be forgotten by a class and it
/// reaches writers this scan cannot classify.
/// </para>
///
/// <para>
/// Presence is not effect, so the hook ships with an observer. <c>DpiHelperScaleObserver</c> fails
/// every test that STARTS on a non-1.0 scale — the external signature of a restore that stopped
/// running — and resets before throwing so a one-off failure does not also fail every later test on
/// the residue. That takes the load off the pins below: a mutation that merely conditions the
/// hook's <c>After</c> is invisible to a declaration check and to a direct call of <c>After</c>,
/// but the observer reddens on it in a real run. What remains unobserved is the observer itself:
/// disabling it silently leaves the restore protecting the suite and removes the detection. That is
/// the deliberate top layer — a detector for the detector would recurse — and it is why the
/// observer's presence is pinned by declaration, reflection and collection here.
/// </para>
///
/// <para>
/// What is pinned below, all four on the compiled assembly or on the source rather than in prose:
/// <see cref="TheRestoreHook_IsInstalledOnThisAssembly"/> and
/// <see cref="TheScaleObserver_IsInstalledOnThisAssembly"/> (declared, compiled, and collected by
/// xUnit for the running test), <see cref="TheRestoreHook_ResetsTheScaleWhenInvoked"/> (the reset
/// itself), and <see cref="EveryWriterBearingProject_InstallsBothScaleGuards"/> (every project
/// holding a writer declares both guards).
/// </para>
///
/// <para>
/// What the scan still cannot see, stated so a green run is not over-read. The needle is literal:
/// a call spelled <c>DpiHelper.Initialize</c> or <c>DpiHelper.Update</c> in the file. A class that
/// reaches the global through production code — <c>DisplayGeometryWatcherTests</c> does, via
/// <c>DisplayGeometryWatcher.Apply</c> — would not be classified as a writer at all if it held no
/// direct call; it is classified today only because one of its tests calls <c>Initialize</c> to
/// restore. The assembly-wide hook closes the ordinal half of that gap unconditionally. The
/// concurrency half is covered only for the one production write path a test can drive
/// (<c>DisplayGeometryWatcher.Apply</c> → <c>DpiHelper.Update</c>; the other production writers
/// are startup-only), whose driving class sits in this collection by construction. A new indirect
/// writer would not be caught here — that is the residual this paragraph records, and the reason
/// the hook, not this scan, is the load-bearing fix.
/// </para>
///
/// <para>
/// The guard obeys its own rule: <see cref="TheRestoreHook_ResetsTheScaleWhenInvoked"/> writes the
/// global to prove the hook resets it, so this class sits in the same collection it requires of
/// every other writer rather than exempting itself.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class DpiHelperWriterIsolationTests
{
    /// <summary>
    /// The call this guard hunts for, assembled from two halves so the needle never appears
    /// verbatim in this file. Written whole, the constant is code rather than a comment, so the
    /// comment stripper cannot remove it and the guard reports ITSELF as an uncollected writer —
    /// the same self-match trap that makes the source-census tests look like DpiHelper readers.
    /// A path-based self-exclusion would work too, but goes stale the moment the file is renamed.
    /// </summary>
    /// <remarks>
    /// <c>Update</c> joined <c>Initialize</c> when display-change handling was added: the class had
    /// exactly one writer for as long as the scale was only ever set at startup, and now has two.
    /// A guard that still hunted only the startup writer would silently stop covering the writer
    /// that fires at arbitrary times — which is the more dangerous of the two.
    /// </remarks>
    private static readonly string[] WriterCalls =
    [
        "DpiHelper" + ".Initialize(",
        "DpiHelper" + ".Update(",
    ];

    /// <summary>The calls, spelled for human-readable failure messages.</summary>
    private static readonly string WriterCallDisplay =
        string.Join(" / ", WriterCalls.Select(c => c + "...)"));

    private static readonly Regex ExclusiveCollectionDefinition = new(
        @"\[\s*CollectionDefinition\s*\(\s*""(?<name>[^""]+)""\s*,\s*DisableParallelization\s*=\s*true\s*\)\s*\]",
        RegexOptions.Compiled);

    private static readonly Regex CollectionMembership = new(
        @"\[\s*Collection\s*\(\s*""(?<name>[^""]+)""\s*\)\s*\]",
        RegexOptions.Compiled);

    /// <summary>
    /// The assembly-level declarations that install the scale guards — the restore hook and the
    /// start-scale observer. Matched as source text because the projects they must appear in are
    /// separate assemblies this one cannot reflect over; the declarations in THIS assembly are
    /// additionally pinned at runtime by <see cref="TheRestoreHook_IsInstalledOnThisAssembly"/> and
    /// <see cref="TheScaleObserver_IsInstalledOnThisAssembly"/>. A rename of either attribute fails
    /// to compile its declaration, and fails this regex loudly in the same build — it cannot go
    /// quiet.
    ///
    /// <para>
    /// Anchored to the start of a line on purpose. Unanchored, it also matched the
    /// <c>[assembly: DpiHelperRestore]</c> inside THIS class's own failure-message strings —
    /// comments are stripped but string literals are not — so deleting the declaration made the
    /// check pass on its own explanation of what was missing. An assembly attribute is a
    /// whole-line declaration, and requiring the line to start with it is what keeps the scan
    /// reading code rather than prose. Measured: the unanchored form stayed green with the
    /// App.Tests declaration removed.
    /// </para>
    /// </summary>
    private static readonly Regex ScaleGuardDeclaration = new(
        @"^\s*\[\s*assembly\s*:\s*(?<guard>DpiHelperRestore|DpiHelperScaleObserver)(?:Attribute)?\s*\]",
        RegexOptions.Compiled | RegexOptions.Multiline);

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
                if (!WriterCalls.Any(w => text.Contains(w, StringComparison.Ordinal))) continue;
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

    /// <summary>
    /// The per-test restore hook is compiled into THIS assembly, not merely written in a source
    /// file: the reflection read would still find nothing if the declaration were present but its
    /// link had been dropped from the project file.
    /// </summary>
    [Fact]
    public void TheRestoreHook_IsInstalledOnThisAssembly() =>
        AssertGuardInstalledAndCollected<DpiHelperRestoreAttribute>(
            "This assembly declares no [assembly: DpiHelperRestore], so nothing resets "
            + "DpiHelper's process-global scale between tests. A writer then changes what every "
            + "later test reads — that is how the scale reached 2.0 for the emoji dwell probe "
            + "and made the category tab steal its sample. Add the declaration (the attribute "
            + "type is linked from tests/TestSupport); do not put the restore back on each "
            + "class, because forgetting it is the defect.",
            "This test runs without the assembly-level DpiHelperRestore hook attached, so "
            + "nothing resets DpiHelper's process-global scale between tests here even though "
            + "the attribute is declared. xUnit no longer collects it from the assembly — "
            + "re-establish the per-test reset (see DpiHelperRestoreAttribute's own summary) "
            + "rather than deleting this check or restoring per class.");

    /// <summary>
    /// The start-scale observer is compiled into THIS assembly and attached to this test. It is the
    /// effect half of the restore: the hook makes a leak harmless, the observer makes a restore
    /// that stopped working fail loudly at the first test that starts corrupted.
    /// </summary>
    [Fact]
    public void TheScaleObserver_IsInstalledOnThisAssembly() =>
        AssertGuardInstalledAndCollected<DpiHelperScaleObserverAttribute>(
            "This assembly declares no [assembly: DpiHelperScaleObserver], so a per-test restore "
            + "that stopped working would corrupt every later test silently instead of failing the "
            + "first test that starts on a non-1.0 scale. Add the declaration (the attribute type "
            + "is linked from tests/TestSupport).",
            "This test runs without the assembly-level DpiHelperScaleObserver attached even though "
            + "the attribute is declared, so a broken restore would go unnoticed. xUnit no longer "
            + "collects it from the assembly — see DpiHelperScaleObserverAttribute's own summary "
            + "rather than deleting this check.");

    /// <summary>
    /// Guards the guard for both scale attributes: declared-and-compiled (reflection) and attached
    /// to the running test (the runner's own attribute list). The second is what neither a source
    /// scan nor a reflection read can establish, and what would rot silently on an xUnit change.
    /// </summary>
    private static void AssertGuardInstalledAndCollected<TGuard>(string notInstalled, string notCollected)
        where TGuard : Attribute
    {
        var declared = typeof(DpiHelperWriterIsolationTests).Assembly
            .GetCustomAttributes(typeof(TGuard), inherit: false);

        Check.WithCustomMessage(notInstalled).That(declared).HasSize(1);

        var collected = ((IXunitTest)TestContext.Current.Test!).TestMethod.BeforeAfterTestAttributes
            .OfType<TGuard>()
            .ToList();

        Check.WithCustomMessage(notCollected).That(collected).HasSize(1);
    }

    /// <summary>
    /// The hook's own behaviour: invoked as xUnit invokes it, it leaves the scale at its default.
    /// Pinned directly so the reset cannot be reduced to a declaration that no longer does anything.
    /// </summary>
    [Fact]
    public void TheRestoreHook_ResetsTheScaleWhenInvoked()
    {
        DpiHelper.Initialize(2.0, 1.5);

        // The hook takes IXunitTest only to satisfy the attribute signature and ignores it, so
        // null proves the reset does not depend on the test context it happens to run in.
        var hook = new DpiHelperRestoreAttribute();
        hook.After(typeof(DpiHelperWriterIsolationTests)
            .GetMethod(nameof(TheRestoreHook_ResetsTheScaleWhenInvoked))!, null!);

        Check.WithCustomMessage(
                "The restore hook did not put DpiHelper back to 1.0/1.0, so it is no longer the "
                + "mechanism the other checks rely on.")
            .That(DpiHelper.ScaleX).IsEqualTo(1.0);
        Check.That(DpiHelper.ScaleY).IsEqualTo(1.0);
    }

    /// <summary>
    /// Every test project holding a direct writer declares both assembly-wide scale guards, so the
    /// ordinal leak is closed for projects whose own test classes this guard cannot watch at
    /// runtime, and a restore that stops working is detected there too.
    /// </summary>
    [Fact]
    public void EveryWriterBearingProject_InstallsBothScaleGuards()
    {
        var writerProjects = new List<string>();
        var violations = new List<string>();

        foreach (var project in TestProjects())
        {
            var files = ProjectSources(project).ToList();
            if (!files.Any(f => WriterCalls.Any(w => f.Text.Contains(w, StringComparison.Ordinal))))
                continue;

            writerProjects.Add(project.Name);

            var declared = files
                .SelectMany(f => ScaleGuardDeclaration.Matches(f.Text).Cast<Match>())
                .Select(m => m.Groups["guard"].Value)
                .ToHashSet(StringComparer.Ordinal);

            if (!declared.Contains("DpiHelperRestore"))
                violations.Add(
                    $"{project.Name}: contains a {WriterCallDisplay} writer but declares no "
                    + "[assembly: DpiHelperRestore], so a test that leaves a non-1.0 scale changes "
                    + "what every later test in that assembly reads.");

            if (!declared.Contains("DpiHelperScaleObserver"))
                violations.Add(
                    $"{project.Name}: declares no [assembly: DpiHelperScaleObserver], so a per-test "
                    + "restore that stopped working would corrupt later tests silently instead of "
                    + "failing the first one that starts on a non-1.0 scale.");
        }

        // Guards the guard: matching nothing would let this pass vacuously.
        Check.WithCustomMessage(
                $"No test project containing {WriterCallDisplay} was found, so this check is "
                + "vacuous. Either the scan broke or the writers moved.")
            .That(writerProjects).Not.IsEmpty();

        Check.WithCustomMessage(
                "A test assembly writes DpiHelper's process-global scale without installing both "
                + "assembly-wide scale guards — the per-test restore hook and its effect observer. "
                + "Per-class restore was the discipline that failed here: five tests in "
                + "DisplayGeometryWatcherTests left the scale at 2.0 while the one test that "
                + "restored did so outside a finally, and the emoji dwell probe then read the wrong "
                + "scale. Add both declarations (the types are linked from tests/TestSupport) "
                + "rather than restoring per class.\n  "
                + string.Join("\n  ", violations))
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
