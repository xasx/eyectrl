namespace EyeCtrl.App.Overlays;

using EyeCtrl.Core;

/// <summary>
/// Single source of truth for note-button geometry, shared by <see cref="NoteWindow"/> (which draws
/// the buttons) and <c>NoteManager</c> (which places the dwell targets over them). Keeping the math
/// here prevents the two from drifting out of alignment.
/// </summary>
internal static class NoteLayout
{
    /// <summary>Window-relative logical X of the button column's center.</summary>
    public static double ButtonColumnCenterX =>
        LayoutConstants.NoteWindowWidth - LayoutConstants.NoteButtonPadding - LayoutConstants.NoteButtonRadius;

    /// <summary>Window-relative logical Y of the first (top) button's center.</summary>
    public static double FirstButtonCenterY =>
        LayoutConstants.NoteTitleBarHeight + LayoutConstants.NoteButtonPadding + LayoutConstants.NoteButtonRadius;

    /// <summary>Vertical distance between consecutive button centers.</summary>
    public static double ButtonStep =>
        (2 * LayoutConstants.NoteButtonRadius) + LayoutConstants.NoteButtonGap;

    /// <summary>Window-relative logical center of button #<paramref name="index"/> (0=close, 1=edit, 2=move).</summary>
    public static (double X, double Y) ButtonCenter(int index) =>
        (ButtonColumnCenterX, FirstButtonCenterY + (index * ButtonStep));
}
