namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public partial class CursorCircleWindow : OverlayWindow
{
    public CursorCircleWindow()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    public void UpdatePosition(ScreenPoint point)
    {
        Left = point.X - Width / 2;
        Top = point.Y - Height / 2;
    }

    public void UpdateAppearance(CursorSettings settings)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(settings.CircleColor);
            Circle.Stroke = new SolidColorBrush(color);
        }
        catch
        {
            Circle.Stroke = Brushes.Lime;
        }

        Circle.StrokeThickness = settings.CircleThickness;
        Circle.Opacity = settings.CircleOpacity;

        // Update circle size
        var size = Math.Max(10, settings.CircleSize);
        Width = size;
        Height = size;
        Circle.Width = size;
        Circle.Height = size;
    }
}
