namespace EyeCtrl.App.Tests.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Controls;

public class DwellArcButtonTests
{
    [StaFact]
    public void Create_PositionsEllipseAroundCenter()
    {
        var btn = DwellArcButton.Create(new Point(100, 100), radius: 50, symbol: "X");
        Check.That(btn.Background.Width).IsEqualTo(100);
        Check.That(Canvas.GetLeft(btn.Background)).IsEqualTo(50);
        Check.That(Canvas.GetTop(btn.Background)).IsEqualTo(50);
    }

    [StaFact]
    public void UpdateProgress_Half_ArcEndsAtBottomOfCircle()
    {
        var btn = DwellArcButton.Create(new Point(100, 100), radius: 50, symbol: null);
        btn.UpdateProgress(0.5);
        var arcRadius = 50 - DwellArcButton.DefaultArcInset;
        Check.That(btn.Arc.Point.X).IsCloseTo(100, 0.001);
        Check.That(btn.Arc.Point.Y).IsCloseTo(100 + arcRadius, 0.001);
        Check.That(btn.Arc.IsLargeArc).IsFalse();
    }

    [StaFact]
    public void UpdateProgress_ThreeQuarters_SetsLargeArc()
    {
        var btn = DwellArcButton.Create(new Point(0, 0), radius: 50, symbol: null);
        btn.UpdateProgress(0.75);
        Check.That(btn.Arc.IsLargeArc).IsTrue();
        var arcRadius = 50 - DwellArcButton.DefaultArcInset;
        Check.That(btn.Arc.Point.X).IsCloseTo(-arcRadius, 0.001);
        Check.That(btn.Arc.Point.Y).IsCloseTo(0, 0.001);
    }

    [StaFact]
    public void UpdateProgress_Zero_CollapsesArcPath()
    {
        var btn = DwellArcButton.Create(new Point(0, 0), radius: 50, symbol: null);
        btn.UpdateProgress(0.5);
        btn.UpdateProgress(0);
        Check.That(btn.ProgressPath.Visibility).IsEqualTo(Visibility.Collapsed);
        btn.UpdateProgress(0.1);
        Check.That(btn.ProgressPath.Visibility).IsEqualTo(Visibility.Visible);
    }

    [StaFact]
    public void AddTo_AddsAllVisualsToCanvas()
    {
        var canvas = new Canvas();
        var btn = DwellArcButton.Create(new Point(10, 10), radius: 20, symbol: "X");
        btn.AddTo(canvas);
        Check.That(canvas.Children.Count).IsEqualTo(3);
    }
}
