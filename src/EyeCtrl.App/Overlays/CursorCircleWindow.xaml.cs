namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;

public partial class CursorCircleWindow : OverlayWindow
{
    // UI Constants
    private const int MinimumCircleSize = 10; // Minimum allowed circle size in pixels

    public CursorCircleWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // keep the cursor circle out of the magnifier/preview capture
        InitializeComponent();
    }

    public void UpdatePosition(PhysicalPoint point)
    {
        Left = DpiHelper.PhysicalToLogicalX(point.X) - Width / 2;
        Top = DpiHelper.PhysicalToLogicalY(point.Y) - Height / 2;
    }

    public void UpdateAppearance(CursorSettings settings)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(settings.CircleColor);
            var brush = new SolidColorBrush(color);
            brush.Freeze();                 // built once per settings change, not per frame
            Circle.Stroke = brush;
        }
        catch
        {
            Circle.Stroke = Tokens.BrushAccent;
        }

        Circle.StrokeThickness = settings.CircleThickness;
        Circle.Opacity = settings.CircleOpacity;

        // Halo is 2px wider on each side so a rim of it shows past the accent ring.
        CircleHalo.Stroke = Tokens.BrushStrokeHalo;
        CircleHalo.StrokeThickness = settings.CircleThickness + 2;
        CircleHalo.Opacity = settings.CircleOpacity;

        // Update circle size
        var size = Math.Max(MinimumCircleSize, settings.CircleSize);
        Width = size;
        Height = size;
        Circle.Width = size;
        Circle.Height = size;
        CircleHalo.Width = size;
        CircleHalo.Height = size;
    }
}
