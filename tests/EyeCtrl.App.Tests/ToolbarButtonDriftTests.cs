namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;

/// <summary>
/// Drift guard: keeps <c>ToolbarWindow</c>'s rendered buttons and <c>ToolbarViewModel</c>'s button
/// list in agreement.
///
/// The two lists are declared independently — the view builds its own array in
/// <c>ToolbarWindow.Initialize</c>, the view-model builds another in its constructor — and nothing
/// tied them together. They drifted: the view-model gained a <c>TaskSwitcher</c> button with a
/// working <c>OnTargetActivated</c> case, but no button was ever added to the view, so the task
/// switcher had no entry point anywhere in the app. It went unnoticed because the only test that
/// counted toolbar buttons is <c>Category=UI</c> and skipped by default — and its count was itself
/// stale by one.
///
/// Two directions are checked:
///   1. every id the VIEW renders must exist in the view-model and have an activation case
///      (otherwise the button is dead on screen);
///   2. every id the VIEW-MODEL defines must be rendered, unless it is listed in
///      <see cref="ReachableElsewhere"/> with its actual entry point.
///
/// Not <c>Category=UI</c> — it only reads source files, so it runs in the headless
/// <c>Category!=UI</c> subset that CI executes.
///
/// The button-array tuples carry <c>ToolbarIconKey</c> member names (Task 2's icon-key channel)
/// rather than Unicode glyphs; the tuple's second element may now be a string literal or a
/// constant reference (e.g. <c>SelectionModeIcons.None</c>). This guard only checks ids, never
/// values — the icon-key value itself is pinned separately by
/// <c>ToolbarIconKeyBridgeTests</c>.
/// </summary>
public class ToolbarButtonDriftTests
{
    private const string ViewRelativePath = "src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs";
    private const string ViewModelRelativePath = "src/EyeCtrl.ViewModels/ToolbarViewModel.cs";

    /// <summary>
    /// View-model buttons deliberately NOT on the toolbar, with the entry point that makes them
    /// reachable. Adding an id here is a decision to keep it off the toolbar — verify the stated
    /// entry point still exists before doing so.
    /// </summary>
    private static readonly Dictionary<string, string> ReachableElsewhere = new(StringComparer.Ordinal)
    {
        ["Calibrate"] = "Settings window action button (CalibrationPageSource sends CalibrationRequestedMessage)",
        ["QuickActions"] = "hot-edge gaze summon (QuickActionsSummonController)",
    };

    /// <summary>
    /// Matches a <c>("Id", value)</c> tuple entry in either button array. The second element may
    /// be a string literal (<c>"Pause"</c>) or a constant reference (<c>SelectionModeIcons.None</c>)
    /// — only the id group is captured, so either form matches.
    /// </summary>
    private static readonly Regex ButtonEntry = new(
        @"\(""(?<id>[A-Za-z][A-Za-z0-9_]*)"",\s*", RegexOptions.Compiled);

    /// <summary>Matches a <c>case "Id":</c> label in the activation switch.</summary>
    private static readonly Regex ActivationCase = new(
        @"case\s+""(?<id>[A-Za-z][A-Za-z0-9_]*)""\s*:", RegexOptions.Compiled);

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(ToolbarButtonDriftTests).Assembly.Location)!);
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

    /// <summary>
    /// Extracts the tuple ids from the <c>var buttons = new (string Id, string ...)[] { … }</c>
    /// initializer, so unrelated string literals elsewhere in the file cannot leak in. Anchored on
    /// the <c>"var buttons = new (string Id, string "</c> prefix rather than the full declaration
    /// text — the view-model's second tuple element is named <c>IconKey</c> and the view's is still
    /// named <c>Symbol</c> (Task 3 renames it), so the two files' declarations no longer match
    /// verbatim. The prefix is all this guard needs; it never reads the element name.
    /// </summary>
    private static IReadOnlyList<string> ButtonIdsFrom(string relativePath)
    {
        var text = ReadRepoFile(relativePath);

        var declaration = text.IndexOf("var buttons = new (string Id, string ", StringComparison.Ordinal);
        Check.WithCustomMessage(
                $"Could not find the button array declaration in {relativePath} (looked for the "
                + "\"var buttons = new (string Id, string ...\" prefix). If it was renamed or "
                + "restructured, update this drift guard rather than deleting it.")
            .That(declaration).IsStrictlyGreaterThan(-1);

        var open = text.IndexOf('{', declaration);
        var close = text.IndexOf("};", open, StringComparison.Ordinal);
        Check.That(close).IsStrictlyGreaterThan(open);

        return ButtonEntry.Matches(text[open..close])
            .Select(m => m.Groups["id"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> ActivationCaseIds()
    {
        var text = ReadRepoFile(ViewModelRelativePath);

        var method = text.IndexOf("protected override void OnTargetActivated", StringComparison.Ordinal);
        Check.That(method).IsStrictlyGreaterThan(-1);

        var switchStart = text.IndexOf("switch (targetId)", method, StringComparison.Ordinal);
        Check.That(switchStart).IsStrictlyGreaterThan(-1);

        var open = text.IndexOf('{', switchStart);
        var close = text.IndexOf("\n        }", open, StringComparison.Ordinal);
        Check.That(close).IsStrictlyGreaterThan(open);

        return ActivationCase.Matches(text[open..close])
            .Select(m => m.Groups["id"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void EveryRenderedButton_ExistsInTheViewModel()
    {
        var orphaned = ButtonIdsFrom(ViewRelativePath)
            .Except(ButtonIdsFrom(ViewModelRelativePath), StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage(
                "These toolbar buttons are rendered by " + ViewRelativePath
                + " but have no entry in " + ViewModelRelativePath
                + ", so they can never receive a dwell: " + string.Join(", ", orphaned))
            .That(orphaned).IsEmpty();
    }

    [Fact]
    public void EveryRenderedButton_HasAnActivationCase()
    {
        var inert = ButtonIdsFrom(ViewRelativePath)
            .Except(ActivationCaseIds(), StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage(
                "These toolbar buttons are rendered but have no case in "
                + "ToolbarViewModel.OnTargetActivated, so dwelling on them does nothing: "
                + string.Join(", ", inert))
            .That(inert).IsEmpty();
    }

    [Fact]
    public void EveryViewModelButton_IsRenderedOrExplicitlyReachableElsewhere()
    {
        var unreachable = ButtonIdsFrom(ViewModelRelativePath)
            .Except(ButtonIdsFrom(ViewRelativePath), StringComparer.Ordinal)
            .Where(id => !ReachableElsewhere.ContainsKey(id))
            .ToList();

        Check.WithCustomMessage(
                "These ToolbarViewModel buttons are not rendered on the toolbar and have no recorded "
                + "alternative entry point, so the feature is unreachable: " + string.Join(", ", unreachable)
                + ". Either add the button to ToolbarWindow.Initialize, or add the id to "
                + nameof(ReachableElsewhere) + " naming the entry point that does reach it.")
            .That(unreachable).IsEmpty();
    }

    [Fact]
    public void TheReachableElsewhereAllowlist_HasNoStaleEntries()
    {
        var viewModelIds = ButtonIdsFrom(ViewModelRelativePath);
        var rendered = ButtonIdsFrom(ViewRelativePath);

        // An allowlist entry is stale if the button no longer exists at all, or if it is now rendered
        // on the toolbar after all — either way the exemption is a lie about the current code.
        var stale = ReachableElsewhere.Keys
            .Where(id => !viewModelIds.Contains(id, StringComparer.Ordinal)
                         || rendered.Contains(id, StringComparer.Ordinal))
            .ToList();

        Check.WithCustomMessage(
                nameof(ReachableElsewhere) + " has entries that no longer describe the code "
                + "(button removed, or now rendered on the toolbar): " + string.Join(", ", stale))
            .That(stale).IsEmpty();
    }
}
