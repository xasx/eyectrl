namespace EyeCtrl.App.Tests;

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using EyeCtrl.App.Services;

/// <summary>
/// The note-gate census (P2 D3): every EyeCtrl window that is CLICK-THROUGH and draws
/// non-transparent content must be classified as suspender, occluder, or exempt-with-reason.
/// A click-through overlay is invisible to <c>WindowFromPoint</c>, so the note gate's three
/// mechanisms (suspend, occlude, own-window) cover it only if this census says which mechanism —
/// a new unclassified window fails here by name.
/// <para>
/// The scope is a SOURCE SCAN for every click-through mechanism, over C# and XAML alike (fixup 01
/// M1): the constructor assignment (<c>IsClickThrough = true</c>), the runtime switch
/// (<c>SetOverlayInteraction(clickThrough: true</c>), and the XAML attribute
/// (<c>IsClickThrough="True"</c>) — reflection can read none of them (review-03 B-2), and the
/// XAML form is the common one for fixed overlays: the track-status card shipped unclassified
/// until this scan widened. The map is pinned, not derived from <c>OverlayInventoryDriftTests</c>
/// (since #217 that set is itself hand-pinned, and a column there would break its last-cell
/// parser — review-02 m-5).
/// </para>
/// </summary>
public class NoteGateCensusTests
{
    // project-rooted path fragment → class + reason. One class per window (review-02 m-4: the
    // banner and the restart card are suspenders only, never occluders).
    private static readonly Dictionary<string, (string Class, string Reason)> Census =
        new(StringComparer.Ordinal)
    {
        // ── suspenders: the gate drops every sample while one is up ──
        ["EyeCtrl.App/Overlays/AdaptationPreviewWindow.xaml.cs"] = (
            "suspender", "the adaptation preview — a RecomputeSuspension reason (ModalSuspended)"),
        ["EyeCtrl.App/Overlays/CalibrationWindow.xaml"] = (
            "suspender", "the same window as the .xaml.cs entry — the XAML attribute declares it click-through from the start; the runtime switch re-applies it when Collecting starts"),
        ["EyeCtrl.App/Overlays/CalibrationWindow.xaml.cs"] = (
            "suspender", "review-03 B-2: turns click-through at runtime on Collecting; the flag comes from CalibrationStateChangedMessage"),
        ["EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs"] = (
            "suspender", "the eye-care banner — a RecomputeSuspension reason (ModalSuspended)"),
        ["EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml.cs"] = (
            "suspender", "full-screen and click-through for the whole break — EyeCareExerciseActiveMessage"),
        ["EyeCtrl.App/Overlays/ManualSelectionOverlayWindow.xaml.cs"] = (
            "suspender", "the full-screen magnifier — ManualSelectionActive/ManualSelectionActionMenuVisible"),
        ["EyeCtrl.App/Overlays/ModesMenuWindow.xaml.cs"] = (
            "suspender", "the modes menu — ModesMenuVM.IsVisible in the composed predicate"),
        ["EyeCtrl.App/Overlays/RestartNoticeWindow.xaml.cs"] = (
            "suspender", "the restart card — a RecomputeSuspension reason (ModalSuspended)"),

        // ── occluders: their visible rects make note targets ineligible, read directly (P2 D3) ──
        ["EyeCtrl.App/Overlays/CaregiverAlertWindow.xaml.cs"] = (
            "occluder", "visible bounds via ICaregiverAlertPresenter.TryGetVisibleBounds"),
        ["EyeCtrl.App/Overlays/DriftNoticeWindow.xaml.cs"] = (
            "occluder", "visible bounds via IDriftNoticePresenter.TryGetVisibleBounds"),
        ["EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml.cs"] = (
            "occluder", "the KEYBOARD instance's button rects ride the keyboard probe (IKeyboardProbe.Rects); the action-menu MODIFIER-bar instance is safe because it shows only with the action menu, a suspending state (review 01 m3)"),
        ["EyeCtrl.App/Overlays/LagMarkWindow.xaml.cs"] = (
            "occluder", "the instrumented button's rects via LagMarkBridge.LiveTargetRects"),
        ["EyeCtrl.App/Overlays/TrackStatusWindow.xaml"] = (
            "occluder", "the opaque track-status card — click-through in XAML, parked top-right for as long as TrackStatusSettings.Enabled; visible bounds via ITrackStatusOverlay.TryGetVisibleBounds (review 01 M1)"),

        // ── exempt, with a reason ──
        ["EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs"] = (
            "exempt", "the chrome zones already drop a button that intersects a note (ActivationOfferEngine's intersection check)"),
        ["EyeCtrl.App/Overlays/CueOverlayWindow.xaml.cs"] = (
            "exempt", "the dwell cue — no area a reader rests on"),
        ["EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs"] = (
            "exempt", "the cursor ring — follows the gaze, no rest area"),
        ["EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs"] = (
            "exempt", "the drag trail — only up during Dragging, a suspending state, and follows the gaze"),
        ["EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml.cs"] = (
            "exempt", "external-dwell feedback — no rest area a note reader targets"),
        ["EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs"] = (
            "exempt", "shows only with the action menu or manual selection (OverlayVmBindingCoordinator) — suspending states"),
        ["EyeCtrl.App/Overlays/ManualSelectionGazeIndicatorWindow.xaml.cs"] = (
            "exempt", "the gaze indicator — follows the gaze inside the magnifier's suspending state"),
        ["EyeCtrl.App/Overlays/PauseIndicatorWindow.xaml"] = (
            "exempt", "shows only while paused (ToolbarVisibilityAdapter) — paused is gate row 2, the notes are frozen (review 01 M1)"),
        ["EyeCtrl.App/Overlays/RepeatCursorWindow.xaml.cs"] = (
            "exempt", "the repeat cursor — only up during RepeatMode, a suspending state, at the live gaze"),
        ["EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml.cs"] = (
            "exempt", "shows only in ScrollMode (ScrollModeController.ApplyCurrentState) — a suspending state (NoteManager.InteractionStateSuspendsNotes); made click-through by main #226"),
        ["EyeCtrl.App/Overlays/TargetHighlightWindow.xaml"] = (
            "exempt", "the action-menu target highlight — shows only with the action menu (OverlayMessageCoordinator), a suspending state (review 01 M1)"),
        };

    // ONE regex over BOTH file types (fixup 01 M1, review 01 n3): the constructor and runtime C#
    // forms, the XAML attribute form ("True", case-insensitive), and a future positional
    // SetOverlayInteraction(true, …). A bare `IsClickThrough = isSomething` re-application is
    // deliberately not matched — the two existing ones (CalibrationWindow's ResetState,
    // OverlayWindow's OnSourceInitialized) re-apply the property value and add no new window.
    private const string ClickThroughPattern =
        @"IsClickThrough\s*=\s*""?true""?|SetOverlayInteraction\(\s*(clickThrough:\s*)?true";

    [Fact]
    public void EveryClickThroughWindow_IsClassified()
    {
        var found = SourceFilesMatchingClickThrough();
        var unclassified = found.Where(f => !Census.ContainsKey(f)).ToList();

        Check.WithCustomMessage(
            "These files turn a window click-through but are not in NoteGateCensusTests' map — "
            + "the note gate cannot see a click-through overlay through WindowFromPoint, so classify "
            + "it (suspender / occluder / exempt-with-reason) and wire that mechanism: "
            + string.Join(", ", unclassified))
            .That(unclassified).IsEmpty();
    }

    [Fact]
    public void TheCensus_HasNoStaleEntries()
    {
        // The scan must SEE every pinned entry — an entry the scan no longer finds is stale, and
        // so is a scan that has gone blind to a declaration style (fixup 01 M1: dropping the XAML
        // half hides all four XAML entries and this fails by name). A bare content re-read would
        // NOT catch that: the file still matches even when the scan skips its extension.
        var found = SourceFilesMatchingClickThrough();
        var stale = Census.Keys
            .Where(path => !found.Contains(path, StringComparer.Ordinal))
            .ToList();

        Check.WithCustomMessage(
            "Census entr(ies) the click-through scan no longer finds: " + string.Join(", ", stale)
            + " — the scan (file set or pattern) has drifted from the census; fix the scan or the entry.")
            .That(stale).IsEmpty();
    }

    /// <summary>
    /// The equivalence pin (P2 D3, review-03 n-1): <c>GazeConsumerInventory.Live()</c> has no
    /// enumerable source list, so the pin classifies the inventory's CONSTRUCTOR PARAMETERS
    /// instead. A new parameter fails here by name until it is mapped to its note-gate class —
    /// the guard that keeps the inventory's source set and the note gate's mechanisms in step
    /// without extracting a shared provider (review-02 M-2 rejected that extraction).
    /// </summary>
    [Fact]
    public void EveryGazeConsumerInventoryParameter_IsClassified()
    {
        var constructor = typeof(GazeConsumerInventory).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var missing = constructor.GetParameters()
            .Where(p => !InventoryClassification.ContainsKey(p.Name!))
            .Select(p => p.Name!)
            .ToList();

        Check.WithCustomMessage(
            "New GazeConsumerInventory parameter(s) without a note-gate classification: "
            + string.Join(", ", missing)
            + " — map each to notes / suspended / external / occluder / infrastructure in this pin.")
            .That(missing).IsEmpty();

        // The pinned map must not drift from the real parameter list either (the staleness half).
        var stale = InventoryClassification.Keys
            .Where(name => constructor.GetParameters().All(p => p.Name != name))
            .ToList();
        Check.WithCustomMessage(
            "Pinned parameter(s) no longer in GazeConsumerInventory's constructor: "
            + string.Join(", ", stale) + " — update the pin.")
            .That(stale).IsEmpty();
    }

    private static readonly Dictionary<string, string> InventoryClassification =
        new(StringComparer.Ordinal)
    {
        // Sources, each mapped to its note-gate class.
        ["keyboardVm"] = "occluder — the keyboard window's rects ride the probe",
        ["edgeBarVm"] = "occluder — the shared bar's rects ride the probe",
        ["lagMarkTargetRects"] = "occluder — LagMarkBridge.LiveTargetRects in SetOccluderSources",
        ["actionMenuVm"] = "suspended — ActionMenuVisible gates the notes",
        ["modesMenuVm"] = "suspended — ModesMenuVM.IsVisible gates the notes",
        ["modBarVm"] = "suspended — shows only with the action menu, a suspending state",
        ["restartNoticeTargetRects"] = "suspended — the card is a RecomputeSuspension reason",
        ["externalTargetRects"] = "external — WindowFromPoint handles third-party windows (review-02 m-3: never an occluder)",
        ["noteTargetRects"] = "notes — the manager's own detector snapshot",
        // Infrastructure, not gaze sources.
        ["settings"] = "infrastructure — the toolbar settings trigger",
        ["overlays"] = "infrastructure — the lazy keyboard-bounds read",
        ["bridge"] = "infrastructure — the toolbar lifecycle trigger",
        ["messenger"] = "infrastructure — the change messages",
        ["logger"] = "infrastructure — optional logging",
    };

    private static List<string> SourceFilesMatchingClickThrough()
    {
        // D3's scope, widened by fixup 01 M1: a scan over src/EyeCtrl.App's .cs AND .xaml files —
        // the click-through declaration lives in either. Fragments are reported repo-relative,
        // matching the census map's keys.
        var app = Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App");
        return Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), ClickThroughPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(f => "EyeCtrl.App/" + f[(app.Length + 1)..].Replace('\\', '/'))
            .ToList();
    }
}
