namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// The window-under-gaze seam (P2 D2): the one gate conjunct <c>WindowFromPoint</c> alone cannot
/// fake in a headless test, so it is an injectable seam like <see cref="IKeyboardProbe"/>. The
/// production implementation is <see cref="NoteHitTester"/>; tests inject a fake returning a
/// chosen HWND. Wired through NoteManager's constructor.
/// <para>
/// Layout rules the P3/P4 geometry tests check (P2 D2): every note hit area lies inside its own
/// window and sits on alpha &gt; 0 pixels, so a root-window comparison at the gaze point is exact.
/// </para>
/// </summary>
public interface INoteHitTester
{
    /// <summary>
    /// The root window that would receive a mouse click at the physical point, or
    /// <see cref="IntPtr.Zero"/> when no window is there. At most one call per gaze sample.
    /// </summary>
    IntPtr RootWindowAt(PhysicalPoint point);
}
