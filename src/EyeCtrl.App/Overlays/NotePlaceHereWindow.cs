namespace EyeCtrl.App.Overlays;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Localization;

/// <summary>
/// The Move state's "Place here" target (P3 D4): one small window per <c>NoteManager</c>, shown
/// beside an accepted settle's centroid and dwelled to re-anchor the note there. It is
/// deliberately NOT click-through — the P2 window gate's own-window conjunct needs the topmost
/// hit-testable window under the gaze to resolve to it, exactly like a note card. Created
/// lazily by the manager, never in DI; <c>NoteManager.Dispose</c> closes it.
/// <para>
/// T6 fixup-06 G1: the label reads at the NOTE's text size (12 DIP was unreadable on the
/// owner's screen) and the window GROWS to fit the measured label — never smaller than the old
/// 150x44 DIP box. The size is a property of the window, so the manager reads it back and every
/// consumer (the dwell target, the on-screen placement, the settle margin) follows the REAL
/// size instead of a constant that no longer describes it.
/// </para>
/// </summary>
public class NotePlaceHereWindow : OverlayWindow
{
    /// <summary>The label's horizontal padding in DIP (the TextBlock's own margin).</summary>
    private const double LabelMarginDip = 8.0;

    /// <summary>The border's thickness in DIP, which the label's space must clear.</summary>
    private const double BorderDip = 2.0;

    private readonly Border _border;
    private readonly TextBlock _label;

    /// <summary>The window's size in DIP: the measured label plus its padding, floored at the
    /// old 150x44 DIP box (T6 fixup-06 G1).</summary>
    public Size SizeDip { get; private set; }

    // T6 fixup-08 R4: set when the manager's show seam skipped the HWND show.
    private bool _displayedWithoutHwnd;

    /// <summary>
    /// Shows the window the way production does, or - with <paramref name="realHwnd"/> false -
    /// only records that it counts as on screen, without an HWND (T6 fixup-08 R4). The Place-here
    /// window builds its content in the constructor and draws nothing on Loaded, so there is no
    /// drawn half to replay; the manager's rect, the settle margin and the dwell target are all
    /// its own and behave as before.
    /// </summary>
    public void ShowForDisplay(bool realHwnd)
    {
        if (realHwnd)
        {
            Show();
            return;
        }

        _displayedWithoutHwnd = true;
    }

    /// <summary>Whether the window counts as on screen: the real HWND's IsVisible in production,
    /// or the seam's own flag when it was never handed to the OS (R4).</summary>
    public bool IsDisplayed => _displayedWithoutHwnd || IsVisible;

    /// <summary>Builds the window for a note text size (T6 fixup-06 G1).</summary>
    /// <param name="textSizeDip">The note's body text size — the label's font size.</param>
    public NotePlaceHereWindow(double textSizeDip)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        IsClickThrough = false;

        _label = new TextBlock
        {
            Text = StringTable.Current.T("overlays.note.placeHere"),
            FontSize = textSizeDip,
            FontFamily = Tokens.FontStandard,
            Foreground = Tokens.BrushTextPrimary,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(LabelMarginDip, 0, LabelMarginDip, 0),
            TextAlignment = TextAlignment.Center
        };

        SizeDip = MeasureSize(textSizeDip, _label.Text);
        Width = SizeDip.Width;
        Height = SizeDip.Height;

        _border = new Border
        {
            Width = SizeDip.Width,
            Height = SizeDip.Height,
            Background = Tokens.BrushSurfaceDark,
            CornerRadius = Tokens.StandardCornerRadius,
            BorderBrush = Tokens.BrushAccent,
            BorderThickness = new Thickness(BorderDip),
            Child = _label
        };
        Content = _border;
    }

    /// <summary>
    /// Re-composes the label at a new text size (T6 fixup-06 G1): the font, the measured size
    /// and the window bounds move together, so the label can never overflow its box.
    /// </summary>
    /// <param name="textSizeDip">The note's body text size — the label's font size.</param>
    public void SetTextSize(double textSizeDip)
    {
        _label.FontSize = textSizeDip;
        SizeDip = MeasureSize(textSizeDip, _label.Text);
        Width = SizeDip.Width;
        Height = SizeDip.Height;
        _border.Width = SizeDip.Width;
        _border.Height = SizeDip.Height;
    }

    /// <summary>
    /// The window's size in DIP for a label at <paramref name="fontSize"/>: the label's measured
    /// width plus its padding and the border, and its measured height plus the border - never
    /// below the old 150x44 DIP box, which was the floor the 12 DIP label needed.
    /// </summary>
    private static Size MeasureSize(double fontSize, string text)
    {
        double labelWidth = 0;
        double labelHeight = 0;
        if (!string.IsNullOrEmpty(text))
        {
            var formatted = new FormattedText(
                text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Tokens.FontStandard.Source), fontSize,
                Tokens.BrushTextPrimary, null, TextFormattingMode.Ideal, 1.0);
            labelWidth = formatted.WidthIncludingTrailingWhitespace;
            labelHeight = formatted.Height;
        }

        double width = Math.Ceiling(Math.Max(
            LayoutConstants.NotePlaceHereWidthDip,
            labelWidth + (2 * LabelMarginDip) + (2 * BorderDip)));
        double height = Math.Ceiling(Math.Max(
            LayoutConstants.NotePlaceHereHeightDip,
            labelHeight + (2 * BorderDip)));
        return new Size(width, height);
    }
}
