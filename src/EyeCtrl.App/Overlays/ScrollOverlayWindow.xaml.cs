namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

public partial class ScrollOverlayWindow : OverlayWindow
{
    public ScrollOverlayWindow()
    {
        InitializeComponent();
    }

    public void Show(ScrollOverlayViewModel vm)
    {
        DataContext = vm;

        OverlayPositioning.CoverPrimaryScreen(this);
        OverlayCanvas.Width = Width;
        OverlayCanvas.Height = Height;

        var anchor = vm.Anchor;
        double anchorLogX = DpiHelper.PhysicalToLogicalX(anchor.X);
        double anchorLogY = DpiHelper.PhysicalToLogicalY(anchor.Y);

        AnchorCrosshairH.X1 = anchorLogX - 15; AnchorCrosshairH.Y1 = anchorLogY;
        AnchorCrosshairH.X2 = anchorLogX + 15; AnchorCrosshairH.Y2 = anchorLogY;
        AnchorCrosshairV.X1 = anchorLogX; AnchorCrosshairV.Y1 = anchorLogY - 15;
        AnchorCrosshairV.X2 = anchorLogX; AnchorCrosshairV.Y2 = anchorLogY + 15;

        Canvas.SetLeft(CursorCircle, anchorLogX - 40);
        Canvas.SetTop(CursorCircle, anchorLogY - 40);
        Canvas.SetLeft(AnchorDot, anchorLogX - 4);
        Canvas.SetTop(AnchorDot, anchorLogY - 4);
        Canvas.SetLeft(VelocityRing, anchorLogX - 40);
        Canvas.SetTop(VelocityRing, anchorLogY - 40);
        Canvas.SetLeft(VelocityRingHalo, anchorLogX - 40);
        Canvas.SetTop(VelocityRingHalo, anchorLogY - 40);

        base.Show();
        Topmost = true;
    }

    public void UpdateFeedback(double dotOffsetX, double dotOffsetY, double velMagnitude)
    {
        double anchorX = Canvas.GetLeft(CursorCircle) + 40;
        double anchorY = Canvas.GetTop(CursorCircle) + 40;
        double dotX = anchorX + dotOffsetX * 24;
        double dotY = anchorY + dotOffsetY * 24;
        Canvas.SetLeft(AnchorDot, dotX - 4);
        Canvas.SetTop(AnchorDot, dotY - 4);

        double thickness = 2 + velMagnitude * 0.2;
        VelocityRing.StrokeThickness = Math.Min(thickness, 12);
        VelocityRingHalo.StrokeThickness = Math.Min(thickness, 12) + 2;
    }
}
