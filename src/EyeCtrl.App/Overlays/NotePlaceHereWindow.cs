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
}
