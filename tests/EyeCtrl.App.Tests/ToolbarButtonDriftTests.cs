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
/// Plus one membership rule (Task 7): ResetAdaptation's band membership is CONDITIONAL on the
/// includeReset flag while its view-model list entry and activation case are STATIC — see
/// <see cref="ResetMembership_IsConditionalInTheBand_AndStaticInTheViewModel"/> for the D11
/// reasoning. The array the band declares keeps the entry either way, so directions 1 and 2
/// apply to it unchanged.
///
/// Not <c>Category=UI</c> — it only reads source files, so it runs in the headless
/// <c>Category!=UI</c> subset that CI executes.
///
/// The second tuple element carries a <c>ToolbarIconKey</c> member name, not a Unicode symbol,
/// since the 2026-08-29 gaze-accessibility change, and may be a constant reference rather than a
/// literal (e.g. <c>SelectionModeIcons.None</c>). This guard anchors on a PREFIX of the
/// declaration and captures only the id, so the element's name and value are both free of it; the
/// key itself is pinned by <c>ToolbarIconKeyBridgeTests</c>, which asserts the parsed enum member
/// rather than mere validity.
/// </summary>
public class ToolbarButtonDriftTests
{
    // The band, not the window: the rendered button array moved to ToolbarBand when the band
    // was split out of ToolbarWindow. ButtonIdsFrom asserts on its anchor prefix, so a path
    // that stops matching fails loudly here rather than silently checking an empty list.
    private const string ViewRelativePath = "src/EyeCtrl.App/Overlays/ToolbarBand.cs";
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

    /// <summary>
    /// Matches <c>ToolbarBand.Build</c>'s signature, whose <c>includeReset</c> parameter is the
    /// membership gate Task 7 added. Anchored on the full parameter list so renaming, re-ordering
    /// or deleting the flag fails this guard rather than silently checking nothing.
    /// </summary>
    private static readonly Regex BuildSignature = new(
        @"internal\s+void\s+Build\s*\(\s*double\s+buttonSize\s*,\s*bool\s+vertical\s*,"
        + @"\s*bool\s+includeReset\s*\)",
        RegexOptions.Compiled);

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
    /// text so a future rename of the second tuple element (currently <c>IconKey</c> in both the
    /// view and the view-model) does not require touching this guard. The prefix is all this guard
    /// needs; it never reads the element name.
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

    /// <summary>
    /// Task 7 (D5/M5): the band's Reset membership is CONDITIONAL — <c>ToolbarBand.Build</c>
    /// takes an <c>includeReset</c> flag and filters the entry out when it is false — while the
    /// view-model's button list and activation case are STATIC (D11): the id and its
    /// <c>case "ResetAdaptation":</c> stay in <c>ToolbarViewModel</c> whatever the flag says.
    ///
    /// The asymmetry is deliberate. The view-model is built once and is consumed by surfaces
    /// that never see the setting (tray, preview wiring, the VM's own tests), so an Enabled flip
    /// rebuilds the BAND, never the list — and the band's array keeps the entry too, so the two
    /// direction checks above keep applying to an id that is only sometimes drawn.
    ///
    /// This test pins both halves so neither can be "simplified" into the other: dropping the
    /// flag makes the band unconditional again (signature check fails); deleting the id from
    /// the array or the view-model makes the list conditional (entry checks fail).
    /// </summary>
    [Fact]
    public void ResetMembership_IsConditionalInTheBand_AndStaticInTheViewModel()
    {
        var band = ReadRepoFile(ViewRelativePath);

        // The band's gate: Build carries the flag, and the filter compares against the NAMED
        // constant — a re-typed "ResetAdaptation" literal here could drift from ResetId and take
        // the ordinary-button branch silently, the exact failure mode ResetId exists to prevent.
        Check.WithCustomMessage(
                "ToolbarBand.Build must take a `bool includeReset` parameter gating the Reset "
                + "entry (Task 7, D5/M5) — band membership is conditional on adaptation being enabled.")
            .That(BuildSignature.IsMatch(band)).IsTrue();
        Check.WithCustomMessage(
                "The band's Reset filter must compare against the ResetId constant, not a "
                + "re-typed string literal.")
            .That(band.Contains("Id != ResetId")).IsTrue();

        // The entry STAYS in the band's array (filtered, not deleted), so direction 1 above
        // still requires it to exist in the view-model — which is what keeps the id wired while
        // the flag is on.
        Check.WithCustomMessage(
                "ResetAdaptation must stay in ToolbarBand's array declaration — membership is a "
                + "runtime filter, not a deletion; the drift checks above read that array.")
            .That(ButtonIdsFrom(ViewRelativePath)).Contains("ResetAdaptation");

        // The view-model half of D11: static list, static case, unconditional.
        Check.WithCustomMessage(
                "ToolbarViewModel's button list must keep ResetAdaptation unconditionally (D11): "
                + "an Enabled flip rebuilds the band, never the view-model's list.")
            .That(ButtonIdsFrom(ViewModelRelativePath)).Contains("ResetAdaptation");
        Check.WithCustomMessage(
                "ToolbarViewModel.OnTargetActivated must keep its ResetAdaptation case even "
                + "though the band can be built without the button (D11).")
            .That(ActivationCaseIds()).Contains("ResetAdaptation");
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
