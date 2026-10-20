namespace EyeCtrl.App.Overlays;

/// <summary>
/// Why the keyboard is being asked to place itself (C1: it never moves under a resting gaze).
/// The session's first placement is whatever request arrives after a show — the show's own
/// layout (<see cref="Keep"/>) or the show path's explicit <see cref="Show"/>. Once placed,
/// only a toolbar appearing (<see cref="Yield"/>), a deliberate <see cref="Flip"/>, or a resize
/// that re-anchors the docked edge (<see cref="Keep"/>) may move it.
/// </summary>
internal enum KeyboardMove
{
    /// <summary>
    /// A show: place at the edge plus the current yield. This is the one place an un-yield
    /// happens. Requested by <see cref="KeyboardWindow.PlaceForShow"/> on the coordinator's
    /// show path.
    /// </summary>
    Show,

    /// <summary>
    /// A toolbar appeared on the keyboard's edge: yield away from it. A zero offset — the
    /// toolbar hid, or it is on another edge — is NOT a move: while open the keyboard never
    /// moves back.
    /// </summary>
    Yield,

    /// <summary>The user flipped the keyboard to the other screen edge: the one deliberate move
    /// while open.</summary>
    Flip,

    /// <summary>
    /// A resize, a sub-layout switch or a settings re-layout. Re-anchors the DOCKED edge (bottom
    /// stays at the work area's bottom, top stays at its top) so growth cannot run off-screen,
    /// keeping the yield currently applied; re-centres horizontally. Never un-yields.
    /// </summary>
    Keep,
}
