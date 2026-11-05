namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// The interaction states whose surfaces OWN the screen while they last — the set both
/// always-up gaze consumers suspend for: the note gate (<c>NoteManager.InteractionStateSuspendsNotes</c>,
/// review 02 confirmed the set is complete) and, since the gaze browser (external review r2 I4),
/// the browse dwell. The browser's under-point gate (<c>WindowFromPoint</c>) sees through
/// click-through windows — the ManualSelection magnifier, the drag trail and the repeat cursor
/// are exactly that — so during those states the point test cannot see them, and the states
/// themselves must gate the accumulation.
/// </summary>
internal static class GazeSuspension
{
    /// <summary>Whether <paramref name="state"/> puts a full-screen click-through surface over
    /// the browser (or otherwise owns the gaze): ActionMenuVisible and the two ManualSelection
    /// states show the menu and the full-screen magnifier, ScrollMode/Dragging/RepeatMode pin
    /// their own controls at the live gaze point. The keyboard is deliberately NOT here —
    /// typing must stay live while it is up (the under-point gate covers it).</summary>
    internal static bool SuspendsGazeSurfaces(InteractionState state) =>
        state is InteractionState.ActionMenuVisible
            or InteractionState.ManualSelectionActive
            or InteractionState.ManualSelectionActionMenuVisible
            or InteractionState.ScrollMode
            or InteractionState.Dragging
            or InteractionState.RepeatMode;
}
