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
/// Deliberately NOT compared against <c>SettingsViewModel.Tabs</c>. Those are a different list with
/// different names (the VM has ExternalDwell/Scroll/Backdrop/Drag/Eye Care; the window has
/// Layout/Models/QuickActions), and the window renders from its own <c>_tabDefs</c> and its own page
/// builders — it does not consume the VM's tab collection. Asserting equality between them would
/// encode a coupling that does not exist.
/// </summary>
public class SettingsTabDriftTests
{
    private const string WindowRelativePath = "src/EyeCtrl.App/Views/SettingsWindow.xaml.cs";
    private const string UiTestRelativePath = "tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs";

    /// <summary>AutomationId prefix applied to each tab in <c>SettingsWindow</c>.</summary>
    private const string TabIdPrefix = "Settings_Tab_";

    /// <summary>Matches the id (first element) of a <c>("Id", "icon", "Label", Builder)</c> tuple.</summary>
    private static readonly Regex TabDefEntry = new(
        @"\(""(?<id>[A-Za-z][A-Za-z0-9_]*)"",\s*""", RegexOptions.Compiled);

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
