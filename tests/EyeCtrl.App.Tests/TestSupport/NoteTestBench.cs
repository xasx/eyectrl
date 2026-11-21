namespace EyeCtrl.App.Tests.TestSupport;

using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Services;

/// <summary>
/// T4's shared note-test geometry (P3): every note manager in the headless tests places against
/// the same FIXED 1920×1200 DIP work area (injected through the manager's work-area seam), and
/// every expected button centre is derived through the SAME placement the manager registered
/// from — the internal <see cref="NoteManager.PlacementForTest"/> seam — so a test's expectation
/// and the manager's truth can never disagree, whatever the placement picked.
/// </summary>
internal static class NoteTestBench
{
    internal static readonly Rect WorkArea = new(0, 0, 1920, 1200);
    internal static Func<Rect> WorkAreaProvider => () => WorkArea;

    /// <summary>The drawn control #<paramref name="slot"/>'s centre in physical px, read from the
    /// note's stored placement and its CURRENT state (K2a: an editing note's controls sit in the
    /// row under the text; every other state reads the column).</summary>
    internal static PhysicalPoint CellCentre(NoteManager manager, string noteId, int slot)
    {
        var placement = manager.PlacementForTest(noteId)
            ?? throw new InvalidOperationException($"No placement for note {noteId}");
        var state = manager.ActiveNotes.FirstOrDefault(n => n.Id == noteId)?.State ?? NoteState.Display;
        return CellCentre(placement, slot, state);
    }

    /// <summary>The control #<paramref name="slot"/>'s centre for the ACTIVE note at
    /// <paramref name="anchor"/>: its own stored placement and current state (K2a: an editing
    /// note's controls sit in the row under the text; everywhere else in the column). The one
    /// shape the tests' anchor-only helpers use, so a state flip cannot leave a held gaze on the
    /// wrong cell.</summary>
    internal static PhysicalPoint CellCentreAtAnchor(NoteManager manager, PhysicalPoint anchor, int slot)
    {
        var note = manager.ActiveNotes.FirstOrDefault(n => n.Anchor.X == anchor.X && n.Anchor.Y == anchor.Y)
            ?? throw new InvalidOperationException($"no active note at {anchor}");
        return CellCentre(manager, note.Id, slot);
    }

    /// <summary>The column button #<paramref name="slot"/>'s centre derived from an anchor alone:
    /// the SAME derivation the manager performs at registration (default right side, no avoid
    /// rects, the bench work area), for tests that only own an anchor. DISPLAY geometry; a test
    /// that may be in Edit uses <see cref="CellCentreAtAnchor"/> with its manager.</summary>
    internal static PhysicalPoint CellCentreFromAnchor(PhysicalPoint anchor, int slot) =>
        CellCentre(PlacementAnchor(anchor), slot, NoteState.Display);

    internal static NotePlacement PlacementAnchor(PhysicalPoint anchor) =>
        NoteLayout.Place(anchor, NoteSide.Right, PlacementCard, WorkArea, Array.Empty<Rect>(), DpiHelper.ScaleX)
            ?? throw new InvalidOperationException("no fitting side");

    // The P4 default card at the control-column floor (the windowless seams measure no text).
    private static Size PlacementCard => new(
        NoteLayout.CardWidthDip(NoteSettings.DefaultTextSizeDip),
        NoteLayout.CardHeightDip(NoteSettings.DefaultTextSizeDip, 0, WorkArea.Height));


    internal static PhysicalPoint CellCentre(NotePlacement placement, int slot, NoteState state)
    {
        // T6 fixup-01: the placement the manager registered from already carries the tag's top
        // strip (WithTagClearance), and ButtonCenter reads the extra top clearance off it, so a
        // grown placement's cell centre lands where the geometry always was.
        var (lx, ly) = NoteLayout.ButtonCenter(placement, slot, state);
        return new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(placement.Window.Left + lx),
            DpiHelper.LogicalToPhysicalY(placement.Window.Top + ly));
    }
}
