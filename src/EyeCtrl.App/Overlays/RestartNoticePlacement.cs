namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.Core.Services;

/// <summary>
/// Where the restart notice card sits (holistic review F5): centred, 48 DIP above the bottom of
/// the work area, like the caregiver alert, except that it must NEVER cover the gaze keyboard.
/// The caregiver alert shares the bottom-centre spot and does not look at the keyboard (it is
/// click-through with no dwell target of its own); this card has a dwell button, and a button over
/// the keys would be dwelled by a gaze meant for a key, and hide the keys it covers.
/// </summary>
internal static class RestartNoticePlacement
{
    /// <summary>The gap between the card's bottom edge and the work area's bottom, in DIP.</summary>
    internal const double MarginDip = 48;

    /// <summary>The clear gap kept between the card and the keyboard, in DIP.</summary>
    internal const double KeyboardGapDip = 24;

    /// <summary>
    /// The shown gaze keyboard's bounds in DIP (everything a gaze there would interact with: the
    /// prediction bar, the category bar and the key grid, as the window itself reports them), or
    /// null when it is not shown or not laid out.
    /// </summary>
    /// <param name="keyboard">The keyboard window.</param>
    /// <returns>The bounds in DIP, or null.</returns>
    internal static Rect? ShownKeyboard(KeyboardWindow keyboard)
    {
        if (!keyboard.IsVisible || !keyboard.TryGetChromeBounds(out var bounds)) return null;
        return new Rect(
            DpiHelper.PhysicalToLogicalX(bounds.X), DpiHelper.PhysicalToLogicalY(bounds.Y),
            DpiHelper.PhysicalToLogicalX(bounds.Width), DpiHelper.PhysicalToLogicalY(bounds.Height));
    }

    /// <summary>
    /// The card's top-left. Without a keyboard, bottom-centre. With one: bottom-centre if that
    /// clears it, otherwise just above the keyboard's top edge, otherwise just below its bottom
    /// edge (a keyboard docked to the top), and at worst the top of the work area. The keyboard
    /// rectangle is inflated by <see cref="KeyboardGapDip"/> so the card never touches it.
    /// </summary>
    /// <param name="workArea">The work area in DIP.</param>
    /// <param name="card">The card's size in DIP.</param>
    /// <param name="keyboard">The shown keyboard's bounds in DIP, or null when it is not shown.</param>
    /// <returns>The card's left and top in DIP.</returns>
    internal static (double Left, double Top) Place(Rect workArea, Size card, Rect? keyboard)
    {
        var left = workArea.Left + (workArea.Width - card.Width) / 2;
        var bottomTop = workArea.Bottom - card.Height - MarginDip;
        if (keyboard is not { } kb) return (left, bottomTop);

        var keepClear = kb;
        keepClear.Inflate(KeyboardGapDip, KeyboardGapDip);

        bool Clear(double top)
        {
            var rect = new Rect(left, top, card.Width, card.Height);
            // A strict overlap: Rect.IntersectsWith counts touching edges, and "just above, a gap
            // clear" puts the card's edge exactly on the inflated keyboard's.
            var overlaps = rect.Right > keepClear.Left && rect.Left < keepClear.Right
                && rect.Bottom > keepClear.Top && rect.Top < keepClear.Bottom;
            return top >= workArea.Top && rect.Bottom <= workArea.Bottom && !overlaps;
        }

        if (Clear(bottomTop)) return (left, bottomTop);
        var above = kb.Top - KeyboardGapDip - card.Height;
        if (Clear(above)) return (left, above);
        var below = kb.Bottom + KeyboardGapDip;
        if (Clear(below)) return (left, below);
        return (left, workArea.Top);
    }
}
