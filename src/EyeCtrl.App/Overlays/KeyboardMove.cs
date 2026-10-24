namespace EyeCtrl.App.Overlays;

/// <summary>
/// Why the keyboard is being asked to place itself (C1: it never moves while the gaze is on it).
/// The session's first placement is whatever request arrives after a show — the show's own
/// layout (<see cref="Keep"/>) or the show path's explicit <see cref="Show"/>. Once placed, only a
/// toolbar appearing (<see cref="Yield"/>), a deliberate <see cref="Flip"/>, a resize that
/// re-anchors the docked edge (<see cref="Keep"/>), or the toolbar's departure once the gaze has
/// been off the window for long enough (<see cref="UnYield"/>) may move it.
/// </summary>
internal enum KeyboardMove
{
    /// <summary>
    /// A show: place at the edge plus the current yield. Requested by
    /// <see cref="KeyboardWindow.PlaceForShow"/> on the coordinator's show path.
    /// </summary>
    Show,

    /// <summary>
    /// A toolbar appeared on the keyboard's edge: yield away from it. A zero offset — the
    /// toolbar hid, or it is on another edge — is NOT a move here: a hiding toolbar does not pull
    /// the keyboard back; <see cref="UnYield"/> does that once the gaze is off the window.
    /// </summary>
    Yield,

    /// <summary>
    /// The toolbar that forced the yield has gone or shrunk AND the gaze has been continuously
    /// outside the keyboard window for <see cref="KeyboardUnYieldGate.OffThresholdUs"/>: move back
    /// to the CURRENT yield — a full un-yield with no toolbar on the edge, a partial one with a
    /// smaller toolbar. Only a magnitude smaller than the applied yield moves the keyboard; it
    /// never yields further. Requested by <see cref="KeyboardWindow.UpdateGaze"/> through its
    /// gaze-off gate.
    /// </summary>
    UnYield,

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
