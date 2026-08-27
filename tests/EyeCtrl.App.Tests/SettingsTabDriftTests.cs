namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;

/// <summary>
/// Drift guard: keeps <c>SettingsTests.SettingsWindow_RendersExactlyTheExpectedTabs</c> honest about
/// what <c>SettingsWindow</c> actually renders.
///
/// That test is <c>Category=UI</c> and therefore skipped unless <c>EYECTRL_UI_TESTS=1</c>, so its
/// expectations rot silently. They had: it expected eight tabs, two of which
/// (<c>ManualSelection</c>, <c>Theme</c>) no longer existed, while <c>_tabDefs</c> had grown to
/// eleven. Nothing failed, because nothing ran.
///
/// The fix is not a better UI test — it is a headless test that validates the UI test's expectation
/// against source. This runs in the <c>Category!=UI</c> subset CI executes, so the skipped test's
/// expectation can no longer drift unnoticed.
///
/// Deliberately NOT compared against <c>SettingsViewModel.Tabs</c>: the window renders from its own
/// <c>_tabDefs</c> and its own page sources; the former parallel tab model in SettingsViewModel was
/// deleted 2026-08. Asserting equality against it would have encoded a coupling that did not exist.
/// </summary>
public class SettingsTabDriftTests
{
    private const string WindowRelativePath = "src/EyeCtrl.App/Views/SettingsWindow.xaml.cs";
    private const string UiTestRelativePath = "tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs";

    /// <summary>AutomationId prefix applied to each tab in <c>SettingsWindow</c>.</summary>
    private const string TabIdPrefix = "Settings_Tab_";

    /// <summary>
    /// Matches the id (first element) of a
    /// <c>("Id", "icon", "Label", "Description", Builder)</c> tuple. Only the id, so adding
    /// members to the tuple cannot break this — which is why the fifth (Description) needed no
    /// change here, unlike <c>SettingsTabLabelFitTests.TabDef</c>, which matches the whole shape.
    /// </summary>
    private static readonly Regex TabDefEntry = new(
        @"\(""(?<id>[A-Za-z][A-Za-z0-9_]*)"",\s*""", RegexOptions.Compiled);

    /// <summary>
    /// Matches the label and header description (third and fourth elements) of a
    /// <c>("Id", "icon", "Label", "Description", Builder)</c> tuple. <c>\s</c> spans newlines, so
    /// entries wrapped across lines still match.
    /// </summary>
    private static readonly Regex TabDefHeader = new(
        @"\(\s*""[A-Za-z][A-Za-z0-9_]*""\s*,\s*""[^""]*""\s*,\s*""(?<label>[^""]*)""\s*,\s*""(?<description>[^""]*)""\s*,",
        RegexOptions.Compiled);

    /// <summary>Matches a <c>"Settings_Tab_Xxx"</c> string literal in the UI test's expected array.</summary>
    private static readonly Regex ExpectedTabLiteral = new(
        @"""Settings_Tab_(?<id>[A-Za-z][A-Za-z0-9_]*)""", RegexOptions.Compiled);

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(SettingsTabDriftTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;

        Check.That(dir).IsNotNull();
        return dir!;
    }

    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.Combine(RepoRoot().FullName, relativePath);
        Check.WithCustomMessage($"Expected source file not found: {relativePath}")
            .That(File.Exists(path)).IsTrue();
        return File.ReadAllText(path);
    }

    /// <summary>Extracts a delimited region so unrelated literals elsewhere in the file cannot leak in.</summary>
    private static string Region(string text, string anchor, string open, string close, string what)
    {
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        Check.WithCustomMessage(
                $"Could not find {what} (anchor: \"{anchor}\"). If it was renamed or restructured, "
                + "update this drift guard rather than deleting it.")
            .That(start).IsStrictlyGreaterThan(-1);

        var openIdx = text.IndexOf(open, start, StringComparison.Ordinal);
        Check.That(openIdx).IsStrictlyGreaterThan(-1);
        var closeIdx = text.IndexOf(close, openIdx, StringComparison.Ordinal);
        Check.That(closeIdx).IsStrictlyGreaterThan(openIdx);

        return text[openIdx..closeIdx];
    }

    /// <summary>Tab ids rendered by <c>SettingsWindow</c>, taken from its <c>_tabDefs</c> initializer.</summary>
    internal static IReadOnlyList<string> RenderedTabIds()
    {
        var region = Region(
            ReadRepoFile(WindowRelativePath), "_tabDefs =", "[", "];", "the _tabDefs initializer");

        return TabDefEntry.Matches(region)
            .Select(m => m.Groups["id"].Value)
            .ToList();
    }

    /// <summary>
    /// Each tab's header label and one-line description, taken from the same <c>_tabDefs</c>
    /// initializer, in declaration order.
    ///
    /// <c>internal</c> so <see cref="SettingsContentFitTests"/> can measure the REAL strings
    /// against the header's fixed height rather than a copy of them: a description edited to
    /// something longer has to be re-measured, and a copied list would not be.
    /// </summary>
    /// <returns>One (label, description) pair per tab, in declaration order.</returns>
    internal static IReadOnlyList<(string Label, string Description)> RenderedTabHeaders()
    {
        var region = Region(
            ReadRepoFile(WindowRelativePath), "_tabDefs =", "[", "];", "the _tabDefs initializer");

        return TabDefHeader.Matches(region)
            .Select(m => (m.Groups["label"].Value, m.Groups["description"].Value))
            .ToList();
    }

    /// <summary>
    /// Every tab carries a header label AND a description. The header is the only place a tab
    /// says what it is for — the sidebar glyphs alone are ambiguous — so a twelfth tab added
    /// without a description would ship a blank second line with nothing failing.
    /// Also guards the guard: if the tuple's shape changes so
    /// <see cref="TabDefHeader"/> stops matching, the counts diverge and this fails loudly
    /// instead of every header assertion passing over an empty list.
    /// </summary>
    [Fact]
    public void EveryRenderedTab_HasALabelAndAHeaderDescription()
    {
        var ids = RenderedTabIds();
        var headers = RenderedTabHeaders();

        Check.WithCustomMessage(
                "Parsed " + headers.Count + " (label, description) pairs out of _tabDefs but "
                + ids.Count + " tab ids. Either a tab is missing its header description (the "
                + "fourth tuple element) or the tuple's shape changed and TabDefHeader no longer "
                + "reads it — in which case repoint the regex, because every header measurement "
                + "in SettingsContentFitTests would otherwise pass over an empty list.")
            .That(headers.Count).IsEqualTo(ids.Count);

        var blank = headers
            .Where(h => string.IsNullOrWhiteSpace(h.Label)
                        || string.IsNullOrWhiteSpace(h.Description))
            .Select(h => "\"" + h.Label + "\"")
            .ToList();

        Check.WithCustomMessage(
                "These tabs have a blank header label or description: "
                + string.Join(", ", blank)
                + ". The page header is the only place a tab explains itself.")
            .That(blank).IsEmpty();
    }

    /// <summary>Tab ids the UI test expects, taken from its <c>expectedTabs</c> array.</summary>
    private static IReadOnlyList<string> UiTestExpectedTabIds()
    {
        var region = Region(
            ReadRepoFile(UiTestRelativePath), "var expectedTabs = new[]", "{", "};",
            "the expectedTabs array");

        return ExpectedTabLiteral.Matches(region)
            .Select(m => m.Groups["id"].Value)
            .ToList();
    }

    [Fact]
    public void TheUiTestExpectation_MatchesTheRenderedTabs()
    {
        var rendered = RenderedTabIds().OrderBy(id => id, StringComparer.Ordinal).ToList();
        var expected = UiTestExpectedTabIds().OrderBy(id => id, StringComparer.Ordinal).ToList();

        var missing = rendered.Except(expected, StringComparer.Ordinal).ToList();
        var phantom = expected.Except(rendered, StringComparer.Ordinal).ToList();

        Check.WithCustomMessage(
                "SettingsTests' expected tab list has drifted from SettingsWindow._tabDefs. "
                + "Rendered but not expected: " + (missing.Count == 0 ? "(none)" : string.Join(", ", missing))
                + ". Expected but not rendered: " + (phantom.Count == 0 ? "(none)" : string.Join(", ", phantom))
                + ". Update the expectedTabs array in " + UiTestRelativePath
                + " (ids are " + TabIdPrefix + "{id} from the _tabDefs first tuple element).")
            .That(missing.Concat(phantom)).IsEmpty();
    }

    [Fact]
    public void RenderedTabIds_AreUnique()
    {
        var ids = RenderedTabIds();
        var duplicates = ids.GroupBy(id => id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        // A duplicate id is silently destructive: _pageBuilders[id] is overwritten by the later entry,
        // and two tab elements end up sharing one AutomationId, so UIA lookups become ambiguous.
        Check.WithCustomMessage(
                "SettingsWindow._tabDefs contains duplicate tab ids: " + string.Join(", ", duplicates))
            .That(duplicates).IsEmpty();
    }

    [Fact]
    public void TabDefsParse_FoundTabs()
    {
        // Guards the guard: if the _tabDefs shape changes so the regex matches nothing, every other
        // assertion here would pass vacuously against two empty sets.
        Check.WithCustomMessage(
                "Parsed zero tabs from " + WindowRelativePath
                + " — the _tabDefs shape changed and this drift guard is no longer reading it.")
            .That(RenderedTabIds()).Not.IsEmpty();
    }
}
