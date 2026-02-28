namespace EyeCtrl.App.Overlays;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EyeCtrl.Core.Models;

public partial class ActivationButtonWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const double ButtonCenter = 50.0;
    private const double ArcRadius = 45.0;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public ActivationButtonWindow()
    {
        InitializeComponent();
        Opacity = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var extStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extStyle | WS_EX_TOOLWINDOW);
    }

    public void ShowAt(ScreenPoint position, double verticalOffset = 180)
    {
        Left = position.X - Width / 2;
        Top = position.Y - Height / 2 - verticalOffset; // Offset above cursor circle

        UpdateDwellProgress(0);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        BeginAnimation(OpacityProperty, fadeIn);
    }

    public new void Hide()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
        fadeOut.Completed += (_, _) => base.Hide();
        BeginAnimation(OpacityProperty, fadeOut);
    }

    public void HideImmediate()
    {
        Opacity = 0;
        base.Hide();
    }

    public void UpdateDwellProgress(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);

        var angle = progress * 360;
        var radians = angle * Math.PI / 180;

        var endX = ButtonCenter + ArcRadius * Math.Sin(radians);
        var endY = ButtonCenter - ArcRadius * Math.Cos(radians);

        ArcSegment.Point = new Point(endX, endY);
        ArcSegment.IsLargeArc = angle > 180;

        // Reset start point if progress is ~0
        if (progress < 0.001)
        {
            ArcSegment.Point = new Point(ButtonCenter, ButtonCenter - ArcRadius);
        }
    }
}
