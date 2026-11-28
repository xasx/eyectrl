namespace EyeCtrl.Core.Keyboard;

/// <summary>
/// F21's runtime fit cap: the largest key size at which the widest row of ANY page of a layout
/// still fits the available width. The QWERTZ letters page grew to 14 units in F21 (⌫ 1.4,
/// Wort löschen 2.6), so at the 2.0x setting its window is wider than a 1920-DIP work area; the
/// effective key size is reduced to keep it on screen. The minimum is taken over EVERY page —
/// never the current one — because a page-dependent key size would move the keys under the
/// resting gaze at a page switch (D13).
///
/// <para>
/// Pure arithmetic, no WPF: the App's sizing code owns the window chrome, this owns the key grid.
/// It never throws and never returns a negative size: a work area too small even for a 0-DIP key
/// yields 0, so the window degrades instead of killing the process.
/// </para>
/// </summary>
public static class KeyboardFitCap
{
    /// <summary>
    /// A row's rendered width in DIP at <paramref name="keySize"/> — the window's own arithmetic
    /// (<c>KeyboardWindow.RebuildKeys</c>): every key takes <c>keySize*w + 4</c> DIP of slot
    /// margin, and a key wider than 1 unit also gains <c>(w-1)*4</c> so its share of the 4-DIP
    /// gutters stays square.
    /// </summary>
    public static double RowWidthDip(IReadOnlyList<KeyDefinition> row, double keySize)
    {
        double width = 0;
        foreach (var key in row)
            width += keySize * key.Width + (key.Width > 1 ? (key.Width - 1) * 4 : 0) + 4;
        return width;
    }

    /// <summary>The widest row of one page, in DIP — the page's grid width.</summary>
    public static double PageWidthDip(SubLayout page, double keySize)
    {
        double widest = 0;
        foreach (var row in page.Rows)
            widest = Math.Max(widest, RowWidthDip(row, keySize));
        return widest;
    }

    /// <summary>
    /// The largest key size (DIP) at which the widest row of any page of
    /// <paramref name="layout"/> fits <paramref name="availableWidthDip"/> (the work area minus
    /// the window chrome). Positive infinity when the layout has no rows, so
    /// <see cref="EffectiveKeySizeDip"/> keeps the requested size.
    /// </summary>
    public static double MaxKeySizeDip(KeyboardLayout layout, double availableWidthDip)
    {
        double max = double.PositiveInfinity;
        foreach (var page in layout.SubLayouts.Values)
        {
            foreach (var row in page.Rows)
            {
                double units = 0;
                double fixedDip = 0;
                foreach (var key in row)
                {
                    units += key.Width;
                    fixedDip += (key.Width > 1 ? (key.Width - 1) * 4 : 0) + 4;
                }

                if (units <= 0) continue;
                max = Math.Min(max, (availableWidthDip - fixedDip) / units);
            }
        }

        return double.IsPositiveInfinity(max) ? max : Math.Max(0, max);
    }

    /// <summary>
    /// The effective key size: the requested size when it already fits, otherwise the largest
    /// size that does. Computed over ALL pages, so the key size is identical on every page.
    /// </summary>
    public static double EffectiveKeySizeDip(
        KeyboardLayout layout, double requestedKeySizeDip, double availableWidthDip) =>
        Math.Min(requestedKeySizeDip, MaxKeySizeDip(layout, availableWidthDip));
}
