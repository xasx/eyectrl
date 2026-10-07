namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;
using Xunit.v3;

/// <summary>
/// The isolation of <see cref="StringTable.Current"/> (critique-01 #8): every Core-referencing
/// test project installs the per-test restore hook and its start-language observer, this assembly
/// has them compiled and collected, and the hook actually resets.
/// </summary>
/// <remarks>
/// The rule is "every test project that (transitively) references EyeCtrl.Core", computed from the
/// csproj graph rather than hand-listed: a new test project cannot silently skip the guards. The
/// ticket named the four projects that lacked an <c>AssemblyInfo.cs</c>; two more Core-referencing
/// projects (Architecture.Tests and Benchmarks.Tests) fall under the same rule and install them
/// too.
/// </remarks>
[Collection("StringTable Tests (App)")]
public class StringTableIsolationTests
{
    /// <summary>
    /// The declarations, matched at line start. Anchoring is load-bearing: unanchored, this regex
    /// also matches the declaration spelled inside this class's own failure-message strings
    /// (comments are stripped, string literals are not) — deleting a real declaration would then
    /// pass on this class's explanation of what was missing.
    /// </summary>
    private static readonly Regex GuardDeclaration = new(
        @"^\s*\[\s*assembly\s*:\s*(?<guard>StringTableRestore|StringTableObserver)(?:Attribute)?\s*\]",
        RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void TheRestoreHook_IsInstalledOnThisAssembly() =>
        AssertGuardInstalledAndCollected<StringTableRestoreAttribute>(
            "This assembly declares no [assembly: StringTableRestore], so nothing resets "
            + "StringTable.Current between tests. A German-path test then changes what every later "
            + "test reads. Add the declaration (the attribute type is linked from tests/TestSupport).",
            "This test runs without the assembly-level StringTableRestore hook attached, so nothing "
            + "resets the process-global table between tests here even though the attribute is declared.");

    [Fact]
    public void TheObserver_IsInstalledOnThisAssembly() =>
        AssertGuardInstalledAndCollected<StringTableObserverAttribute>(
            "This assembly declares no [assembly: StringTableObserver], so a restore that stopped "
            + "working would feed German into later tests silently instead of failing the first test "
            + "that starts non-English. Add the declaration (the attribute type is linked from "
            + "tests/TestSupport).",
            "This test runs without the assembly-level StringTableObserver attached even though the "
            + "attribute is declared, so a broken restore would go unnoticed.");

    [Fact]
    public void TheRestoreHook_ResetsTheGlobalWhenInvoked()
    {
        StringTable.Activate(StringTable.Create(
            UiLanguage.German,
            System.Globalization.CultureInfo.GetCultureInfo("de-DE"),
            StringTableContent.ParseEnglish("{}")));
        Check.That(StringTable.Current.Language).IsEqualTo(UiLanguage.German);

        // The hook takes IXunitTest only to satisfy the attribute signature and ignores it, so
        // null proves the reset does not depend on the test context it happens to run in.
        var hook = new StringTableRestoreAttribute();
        hook.After(typeof(StringTableIsolationTests)
            .GetMethod(nameof(TheRestoreHook_ResetsTheGlobalWhenInvoked))!, null!);

        Check.WithCustomMessage(
                "The restore hook did not put StringTable.Current back to embedded English, so it is "
                + "no longer the mechanism the other checks rely on.")
            .That(StringTable.Current).IsSameReferenceAs(StringTable.EmbeddedEnglish);
    }

    [Fact]
    public void TheDefaultGlobal_IsEmbeddedEnglish()
    {
        Check.That(StringTable.Current).IsSameReferenceAs(StringTable.EmbeddedEnglish);
        Check.That(StringTable.Current.Language).IsEqualTo(UiLanguage.English);
    }

    [Fact]
    public void EveryCoreReferencingTestProject_InstallsBothGuards()
    {
        var projects = Projects();
        var graph = projects.ToDictionary(p => p.Name, p => p.References, StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();
        var required = new List<string>();

        foreach (var project in projects.Where(p =>
                     p.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                     && p.Path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
                         StringComparison.OrdinalIgnoreCase)))
        {
            if (!ReachesCore(project.Name, graph, [])) continue;
            required.Add(project.Name);

            var declared = SourcesIn(Path.GetDirectoryName(project.Path)!)
                .SelectMany(text => GuardDeclaration.Matches(text).Cast<Match>())
                .Select(match => match.Groups["guard"].Value)
                .ToHashSet(StringComparer.Ordinal);

            if (!declared.Contains("StringTableRestore"))
            {
                violations.Add(
                    $"{project.Name}: references EyeCtrl.Core but declares no [assembly: "
                    + "StringTableRestore], so a test that activates a German table changes what "
                    + "every later test in that assembly reads.");
            }

            if (!declared.Contains("StringTableObserver"))
            {
                violations.Add(
                    $"{project.Name}: declares no [assembly: StringTableObserver], so a per-test "
                    + "restore that stopped working would corrupt later tests silently.");
            }
        }

        // Guards the guard: matching nothing would pass vacuously.
        foreach (var expected in new[]
                 {
                     "EyeCtrl.App.Tests", "EyeCtrl.Core.Tests", "EyeCtrl.Controls.Tests",
                     "EyeCtrl.Supervisor.Tests", "EyeCtrl.Platform.Tests", "EyeCtrl.EyeCare.Tests",
                     "EyeCtrl.ViewModels.Tests", "EyeCtrl.LM.Tests", "EyeCtrl.Architecture.Tests",
                     "EyeCtrl.Benchmarks.Tests"
                 })
        {
            Check.WithCustomMessage($"Expected '{expected}' to be a Core-referencing test project")
                .That(required).Contains(expected);
        }

        // The SDK test projects do not reference Core; requiring the guard there would force a
        // Core reference into the SDK line, which exists to prove standalone consumption.
        Check.That(required).Not.Contains("EyeCtrl.Sdk.Abstractions.Tests");
        Check.That(required).Not.Contains("EyeCtrl.Sdk.Wpf.Tests");
        Check.That(required).Not.Contains("EyeCtrl.Sdk.WinUI.Tests");

        Check.WithCustomMessage(
                "A test project that references EyeCtrl.Core installs no assembly-wide string-table "
                + "guards. Add both declarations to its AssemblyInfo.cs (the attribute types are "
                + "linked from tests/TestSupport).\n  " + string.Join("\n  ", violations))
            .That(violations).IsEmpty();
    }

    private static void AssertGuardInstalledAndCollected<TGuard>(string notInstalled, string notCollected)
        where TGuard : Attribute
    {
        var declared = typeof(StringTableIsolationTests).Assembly
            .GetCustomAttributes(typeof(TGuard), inherit: false);

        Check.WithCustomMessage(notInstalled).That(declared).HasSize(1);

        var collected = ((IXunitTest)TestContext.Current.Test!).TestMethod.BeforeAfterTestAttributes
            .OfType<TGuard>()
            .ToList();

        Check.WithCustomMessage(notCollected).That(collected).HasSize(1);
    }

    private static bool ReachesCore(string project, Dictionary<string, List<string>> graph, HashSet<string> visited)
    {
        if (string.Equals(project, "EyeCtrl.Core", StringComparison.OrdinalIgnoreCase)) return true;
        if (!visited.Add(project)) return false;
        if (!graph.TryGetValue(project, out var references)) return false;
        return references.Any(reference => ReachesCore(reference, graph, visited));
    }

    private static List<(string Name, string Path, List<string> References)> Projects()
    {
        var root = SourceScanner.RepoRoot().FullName;
        var results = new List<(string, string, List<string>)>();
        foreach (var baseDirectory in new[] { "src", "tests", "benchmarks" })
        {
            var directory = Path.Combine(root, baseDirectory);
            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)) continue;
                var document = XDocument.Load(file);
                var references = document.Descendants("ProjectReference")
                    .Select(element => (string?)element.Attribute("Include"))
                    .Where(include => include is not null)
                    .Select(include => Path.GetFileNameWithoutExtension(include!))
                    .ToList();
                results.Add((Path.GetFileNameWithoutExtension(file), file, references));
            }
        }
        return results;
    }

    private static IEnumerable<string> SourcesIn(string projectDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            yield return SourceScanner.StripComments(File.ReadAllText(file));
        }
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
}

/// <summary>
/// Gives the tests that touch <see cref="StringTable.Current"/> (the isolation tests' direct
/// resets, and the markup-extension tests in the Controls suite) an exclusive xUnit phase, so no
/// parallel test can read the global mid-change.
/// </summary>
[CollectionDefinition("StringTable Tests (App)", DisableParallelization = true)]
public class StringTableAppCollection
{
}
