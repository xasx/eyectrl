namespace EyeCtrl.App.Overlays;

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
/// </summary>
public class NotePlaceHereWindow : OverlayWindow
{
    private readonly Border _border;
    private readonly TextBlock _label;

    public NotePlaceHereWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        IsClickThrough = false;
        Width = LayoutConstants.NotePlaceHereWidthDip;
        Height = LayoutConstants.NotePlaceHereHeightDip;

        _label = new TextBlock
        {
            Text = StringTable.Current.T("overlays.note.placeHere"),
            FontSize = Tokens.FontSizeBody,
            Foreground = Tokens.BrushTextPrimary,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            TextAlignment = TextAlignment.Center
        };
        _border = new Border
        {
            Width = LayoutConstants.NotePlaceHereWidthDip,
            Height = LayoutConstants.NotePlaceHereHeightDip,
            Background = Tokens.BrushSurfaceDark,
            CornerRadius = Tokens.StandardCornerRadius,
            BorderBrush = Tokens.BrushAccent,
            BorderThickness = new Thickness(2),
            Child = _label
        };
        Content = _border;
    }

    // The show seam (T6 fixup-08 R4, moved down by sync 4): set when the manager skipped the HWND show.
    private bool _displayedWithoutHwnd;

    /// <summary>
    /// Shows the window the way production does, or - with <paramref name="realHwnd"/> false - only
    /// records that it counts as on screen, without an HWND. The Place-here window builds its
    /// content in the constructor and draws nothing on Loaded, so there is no drawn half to replay;
    /// the manager's rect, the settle margin and the dwell target are all its own, as before.
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
    /// or the seam's own flag when it was never handed to the OS.</summary>
    public bool IsDisplayed => _displayedWithoutHwnd || IsVisible;
}
