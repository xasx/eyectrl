namespace EyeCtrl.App.Tests;

using System.IO;
using System.Reflection;
using EyeCtrl.App.Services;

/// <summary>
/// The note-gate census (P2 D3): every EyeCtrl window that is CLICK-THROUGH and draws
/// non-transparent content must be classified as suspender, occluder, or exempt-with-reason.
/// A click-through overlay is invisible to <c>WindowFromPoint</c>, so the note gate's three
/// mechanisms (suspend, occlude, own-window) cover it only if this census says which mechanism —
/// a new unclassified window fails here by name.
/// <para>
/// The scope is a SOURCE SCAN for both click-through mechanisms — the constructor assignment
/// (<c>IsClickThrough = true</c>) and the runtime switch
/// (<c>SetOverlayInteraction(clickThrough: true</c>) — because reflection can read neither
/// (review-03 B-2): the calibration window is hit-testable at construction and turns
/// click-through when Collecting starts. The map is pinned, not derived from
/// <c>OverlayInventoryDriftTests</c> (since #217 that set is itself hand-pinned, and a column
/// there would break its last-cell parser — review-02 m-5).
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
            "occluder", "the bar's button rects ride the keyboard probe (IKeyboardProbe.Rects)"),
        ["EyeCtrl.App/Overlays/LagMarkWindow.xaml.cs"] = (
            "occluder", "the instrumented button's rects via LagMarkBridge.LiveTargetRects"),

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
        ["EyeCtrl.App/Overlays/RepeatCursorWindow.xaml.cs"] = (
            "exempt", "the repeat cursor — only up during RepeatMode, a suspending state, at the live gaze"),
        };

    private static readonly string[] ClickThroughPatterns =
    [
        "IsClickThrough = true",
        "SetOverlayInteraction(clickThrough: true",
    ];

    [Fact]
    public void EveryClickThroughWindow_IsClassified()
    {
        var found = SourceFilesContainingAny(ClickThroughPatterns);
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
        var src = Path.Combine(SourceScanner.RepoRoot().FullName, "src");
        foreach (var (path, entry) in Census)
        {
            var full = Path.Combine(src, path.Replace('/', Path.DirectorySeparatorChar));
            Check.WithCustomMessage($"Census entry '{path}' ({entry.Reason}) no longer exists on disk.")
                .That(File.Exists(full)).IsTrue();
            Check.WithCustomMessage(
                    $"Census entry '{path}' ({entry.Reason}) no longer sets click-through by either "
                    + "mechanism — update the classification or remove the entry.")
                .That(ClickThroughPatterns.Any(p => File.ReadAllText(full).Contains(p))).IsTrue();
        }
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

    private static List<string> SourceFilesContainingAny(string[] markers)
    {
        // D3's scope: a scan over src/EyeCtrl.App — the overlay windows live there. Fragments are
        // reported repo-relative, matching the census map's keys.
        var app = Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App");
        return Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => markers.Any(m => File.ReadAllText(f).Contains(m)))
            .Select(f => "EyeCtrl.App/" + f[(app.Length + 1)..].Replace('\\', '/'))
            .ToList();
    }
}
