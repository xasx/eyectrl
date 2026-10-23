namespace EyeCtrl.App.Services.Notes;

/// <summary>The caretaker input kinds a note control accepts (T6 P5). Touch is the caretaker's
/// finger; Mouse is a physical pointer click — which also covers EyeCtrl's own synthesized
/// clicks, hence the pause requirement.</summary>
internal enum NoteInputKind
{
    Touch,
    Mouse,
}

/// <summary>
/// T6 P5: whether a caretaker input on a note control runs the control's dwell action. Pure on
/// purpose — the accept/ignore decision is exhaustively testable without a window.
///
/// <para>
/// <b>Touch</b> acts exactly like the control's dwell minus the gaze machinery: no pause
/// requirement (a caretaker touches deliberately, and pausing is the OWNER's action), no gate,
/// latch or dwell floor — but a hidden or unregistered control never acts, so a control that
/// the manager has not drawn and registered (⌨ while the keyboard is up, Move's Other side
/// while no side fits, a collapsed state row) is inert.
/// </para>
///
/// <para>
/// <b>Mouse</b> acts ONLY while EyeCtrl is paused (the owner's gaze must not be fighting a
/// stray click; EyeCtrl's own synthesized clicks fire while running, so the pause requirement
/// excludes them by construction). A mouse event promoted from touch
/// (<c>StylusDevice != null</c>) is ignored ALWAYS — the touch already acted at TouchUp, and
/// the promotion would run the action a second time. The same hidden/unregistered rule as
/// touch applies.
/// </para>
/// </summary>
internal static class NoteInputPolicy
{
    /// <summary>Whether the input runs the control's action exactly once.</summary>
    /// <param name="kind">Touch or Mouse.</param>
    /// <param name="paused">Whether EyeCtrl is currently paused (the pause poll seam).</param>
    /// <param name="fromStylus">Whether the mouse event was promoted from a touch
    /// (<c>StylusDevice != null</c>).</param>
    /// <param name="controlVisible">Whether the control is currently DRAWN visible.</param>
    /// <param name="controlRegistered">Whether the control's dwell target is currently
    /// registered with the manager.</param>
    public static bool Accepts(
        NoteInputKind kind, bool paused, bool fromStylus, bool controlVisible, bool controlRegistered) =>
        kind switch
        {
            NoteInputKind.Touch => controlVisible && controlRegistered,
            NoteInputKind.Mouse => paused && !fromStylus && controlVisible && controlRegistered,
            _ => false,
        };
}
