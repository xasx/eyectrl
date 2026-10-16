namespace EyeCtrl.App.Diagnostics.SubPixel;

using EyeCtrl.Core.Models;

/// <summary>
/// The probe window as C7 sees it (build plan C6/C14). The production implementation is
/// <see cref="SubPixelProbeWindow"/>; W1's recording host returns a double that models WPF's close
/// semantics as measured in review 05 E4.
///
/// <para>
/// <b>"Already closed" means <see cref="ClosedObserved"/> is set, never that <see cref="Close"/>
/// was called</b> (review 05 F3/F4). WPF raises <c>Closed</c> synchronously inside <c>Close()</c>,
/// so the flag is set by the window's own closed handler before it invokes the session-end callback.
/// The fake can swallow a <c>Close()</c> issued during <c>Show()</c>, where the real behaviour is
/// unmeasured — the after-Show close path exists for exactly that case.
/// </para>
/// </summary>
internal interface IProbeWindow
{
    /// <summary><c>true</c> once the window's <c>Closed</c> event has been observed.</summary>
    bool ClosedObserved { get; }

    /// <summary><c>true</c> once <see cref="Show"/> has returned without the window being closed.</summary>
    bool IsShown { get; }

    /// <summary>
    /// The latest filtered gaze position, in physical pixels. C7 forwards it from
    /// <c>GazeCursorMovedMessage</c>; the window uses it only while a live interval is drawn.
    /// </summary>
    /// <param name="positionPhys">Filtered gaze position, physical screen pixels.</param>
    void UpdateGaze(PhysicalPoint positionPhys);

    /// <summary>
    /// Shows the window. The window end callback (wired at construction) is never invoked from here
    /// directly; production WPF may raise <c>Closed</c> synchronously if something re-enters
    /// <see cref="Close"/> during the show.
    /// </summary>
    void Show();

    /// <summary>
    /// Closes the window, raising the closed path at most once. A re-entrant close is a no-op, as
    /// WPF's own <c>Close()</c> is (review 05 E4).
    /// </summary>
    void Close();
}
