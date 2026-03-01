namespace EyeCtrl.App.Overlays;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public partial class CursorCircleWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public CursorCircleWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var extStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
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
