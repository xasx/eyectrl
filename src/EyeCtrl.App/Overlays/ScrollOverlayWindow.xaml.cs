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

        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        Left = screen.Bounds.X / DpiHelper.ScaleX;
        Top = screen.Bounds.Y / DpiHelper.ScaleY;
        Width = screen.Bounds.Width / DpiHelper.ScaleX;
        Height = screen.Bounds.Height / DpiHelper.ScaleY;
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

        var stopCenter = vm.StopButtonCenter;
        double stopLogX = DpiHelper.PhysicalToLogicalX(stopCenter.X);
        double stopLogY = DpiHelper.PhysicalToLogicalY(stopCenter.Y);
        Canvas.SetLeft(StopButton, stopLogX - 25);
        Canvas.SetTop(StopButton, stopLogY - 25);
        Canvas.SetLeft(StopButtonProgressArc, stopLogX - 25);
        Canvas.SetTop(StopButtonProgressArc, stopLogY - 25);
        Canvas.SetLeft(StopLabel, stopLogX - 12);
        Canvas.SetTop(StopLabel, stopLogY - 14);

        base.Show();
        Topmost = true;
    }

    public void UpdateFeedback(double dotOffsetX, double dotOffsetY,
        double velMagnitude, double stopProgress)
    {
        double anchorX = Canvas.GetLeft(CursorCircle) + 40;
        double anchorY = Canvas.GetTop(CursorCircle) + 40;
        double dotX = anchorX + dotOffsetX * 24;
        double dotY = anchorY + dotOffsetY * 24;
        Canvas.SetLeft(AnchorDot, dotX - 4);
        Canvas.SetTop(AnchorDot, dotY - 4);

        double thickness = 2 + velMagnitude * 0.2;
        VelocityRing.StrokeThickness = Math.Min(thickness, 12);

        if (stopProgress > 0)
        {
            StopButtonProgressArc.Visibility = Visibility.Visible;
            double angleDeg = stopProgress * 360;
            double angleRad = (angleDeg - 90) * Math.PI / 180;
            double endX = 25 + 25 * Math.Cos(angleRad);
            double endY = 25 + 25 * Math.Sin(angleRad);
            var arc = new ArcSegment
            {
                Point = new Point(endX, endY),
                Size = new Size(25, 25),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = angleDeg > 180
            };
            var fig = new PathFigure { StartPoint = new Point(25, 0) };
            fig.Segments.Add(arc);
            StopButtonProgressArc.Data = new PathGeometry { Figures = { fig } };
        }
        else
        {
            StopButtonProgressArc.Visibility = Visibility.Hidden;
        }
    }
}
