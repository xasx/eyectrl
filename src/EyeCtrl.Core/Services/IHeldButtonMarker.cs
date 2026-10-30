namespace EyeCtrl.Core.Services;

/// <summary>
/// Records, ACROSS PROCESSES, that EyeCtrl pressed the left mouse button and has not released it.
/// <para>
/// The OS button state outlives the process: a drag holds the button down through <c>SendInput</c>,
/// and a hard kill mid-drag (the Supervisor's force-kill, Task Manager, a native crash) runs none of
/// the in-process release paths. Without a record that survives the process, the successor starts in
/// FreeLook where the gaze engine moves the cursor, and the user's gaze becomes a drag they cannot
/// stop. The marker is that record: written on press, cleared on every release, and read by the
/// Supervisor after a force-kill and by the App at startup.
/// </para>
/// <para>
/// The seam is what keeps tests off the real app-data file (and off <c>SendInput</c>): production
/// passes a <see cref="FileHeldButtonMarker"/>, tests pass a fake or a marker over a temp path.
/// </para>
/// </summary>
public interface IHeldButtonMarker
{
    /// <summary>
    /// True when a press was recorded and no matching release has cleared it. A <c>false</c> here
    /// must mean NO button-up is sent: the user may be holding a physical mouse.
    /// </summary>
    bool IsSet { get; }

    /// <summary>
    /// Records that EyeCtrl now holds the left button. Called after the press was actually sent,
    /// so the marker never claims a press the OS never saw.
    /// </summary>
    void Set();

    /// <summary>
    /// Records that the release was sent. Called only after the button-up succeeded, so a failed
    /// release leaves the marker in place for the next attempt.
    /// </summary>
    void Clear();
}
