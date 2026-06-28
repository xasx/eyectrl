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
    private readonly ArcSegment _stopArc = new()
    {
        Size = new Size(25, 25),
        SweepDirection = SweepDirection.Clockwise
    };
    private readonly PathFigure _stopFigure = new() { StartPoint = new Point(25, 0) };
    private readonly PathGeometry _stopGeometry = new();

    public ScrollOverlayWindow()
    {
        InitializeComponent();
        _stopGeometry.Figures.Add(_stopFigure);
        _stopFigure.Segments.Add(_stopArc);
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
            _stopArc.Point = new Point(endX, endY);
            _stopArc.IsLargeArc = angleDeg > 180;
            StopButtonProgressArc.Data = _stopGeometry;
        }
        else
        {
            StopButtonProgressArc.Visibility = Visibility.Hidden;
        }
    }
}
