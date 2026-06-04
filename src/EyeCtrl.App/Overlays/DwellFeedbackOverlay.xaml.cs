namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.Core.Services;

public partial class DwellFeedbackOverlay : OverlayWindow
{
    public DwellFeedbackOverlay()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    public void ShowAt(double physX, double physY, double physW, double physH)
    {
        Left = DpiHelper.PhysicalToLogicalX(physX);
        Top = DpiHelper.PhysicalToLogicalY(physY);
        Width = DpiHelper.PhysicalToLogicalX(physW);
        Height = DpiHelper.PhysicalToLogicalY(physH);
        ProgressFill.Width = 0;
        ProgressBorder.Visibility = Visibility.Visible;
        Show();
    }

    public void SetProgress(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        ProgressFill.Width = ProgressBorder.ActualWidth * progress;
    }

    public void HideFeedback()
    {
        ProgressBorder.Visibility = Visibility.Hidden;
        Hide();
    }
}
