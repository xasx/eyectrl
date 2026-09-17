namespace EyeCtrl.App.Services;

/// <summary>
/// Pure math for the keyboard's yield to a summoned toolbar (spec B3 #6, ledger #5): when the
/// toolbar is on screen at the SAME vertical edge as the keyboard, the keyboard shifts one
/// toolbar-extent toward screen center; otherwise no shift. Extracted from
/// <c>KeyboardWindow.RepositionOnScreen</c> so the math is headless-testable.
/// </summary>
internal static class KeyboardYield
{
    /// <param name="keyboardEdge">The keyboard's ScreenEdge string ("Top"/"Bottom", any case).</param>
    /// <param name="toolbarOccupied">Whether the toolbar window is on screen.</param>
    /// <param name="toolbarEdge">The toolbar's effective edge; "none" never matches.</param>
    /// <param name="extentDip">Toolbar thickness in DIP (window ActualHeight).</param>
    /// <param name="keyboardTopDip">The keyboard's un-yielded Top from PlaceAtEdge.</param>
    /// <param name="keyboardHeightDip">Keyboard window height in DIP.</param>
    /// <param name="workAreaTopDip">Work-area top in DIP.</param>
    /// <param name="workAreaBottomDip">Work-area bottom in DIP.</param>
    /// <returns>Downward shift in DIP (negative = upward), clamped so the keyboard stays in the work area.</returns>
    internal static double VerticalOffset(
        string keyboardEdge, bool toolbarOccupied, string toolbarEdge,
        double extentDip, double keyboardTopDip, double keyboardHeightDip,
        double workAreaTopDip, double workAreaBottomDip)
    {
        if (!toolbarOccupied || extentDip <= 0) return 0;

        var kb = keyboardEdge?.ToLowerInvariant() ?? "bottom";
        var tb = toolbarEdge?.ToLowerInvariant() ?? "none";
        if (!string.Equals(kb, tb, StringComparison.Ordinal)) return 0;

        var desired = kb == "top" ? extentDip : -extentDip;
        var newTop = keyboardTopDip + desired;
        var maxTop = workAreaBottomDip - keyboardHeightDip;
        if (newTop < workAreaTopDip) desired += workAreaTopDip - newTop;
        if (newTop > maxTop) desired -= newTop - maxTop;
        return desired;
    }
}
