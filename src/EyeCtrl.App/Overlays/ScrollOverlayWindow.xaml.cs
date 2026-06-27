namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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

        var anchor = vm.Anchor;

        AnchorCrosshairH.X1 = anchor.X - 15; AnchorCrosshairH.Y1 = anchor.Y;
        AnchorCrosshairH.X2 = anchor.X + 15; AnchorCrosshairH.Y2 = anchor.Y;
        AnchorCrosshairV.X1 = anchor.X; AnchorCrosshairV.Y1 = anchor.Y - 15;
        AnchorCrosshairV.X2 = anchor.X; AnchorCrosshairV.Y2 = anchor.Y + 15;

        Canvas.SetLeft(CursorCircle, anchor.X - 40);
        Canvas.SetTop(CursorCircle, anchor.Y - 40);
        Canvas.SetLeft(AnchorDot, anchor.X - 4);
        Canvas.SetTop(AnchorDot, anchor.Y - 4);
        Canvas.SetLeft(VelocityRing, anchor.X - 40);
        Canvas.SetTop(VelocityRing, anchor.Y - 40);

        var stopCenter = vm.StopButtonCenter;
        Canvas.SetLeft(StopButton, stopCenter.X - 25);
        Canvas.SetTop(StopButton, stopCenter.Y - 25);
        Canvas.SetLeft(StopButtonProgressArc, stopCenter.X - 25);
        Canvas.SetTop(StopButtonProgressArc, stopCenter.Y - 25);
        Canvas.SetLeft(StopLabel, stopCenter.X - 12);
        Canvas.SetTop(StopLabel, stopCenter.Y - 14);

        Show();
        Topmost = true;
    }

    public void UpdateFeedback(double dotOffsetX, double dotOffsetY,
        double velMagnitude, double stopProgress)
    {
        double anchorX = Canvas.GetLeft(CursorCircle) + 40;
        double anchorY = Canvas.GetTop(CursorCircle) + 40;
        double circleRadius = 40;
        double dotX = anchorX + dotOffsetX * circleRadius * 0.6;
        double dotY = anchorY + dotOffsetY * circleRadius * 0.6;
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
