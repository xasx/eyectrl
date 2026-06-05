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
        // Note: don't reset ProgressFill here — ShowAt is also called to reposition a target that
        // moves mid-dwell, and the immediately-following SetProgress drives the fill width.
        ProgressBorder.Visibility = Visibility.Visible;
        if (!IsVisible) Show();
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
