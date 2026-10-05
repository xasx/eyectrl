namespace EyeCtrl.App.Tests.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;

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
        Check.That(canvas.Children.Count).IsEqualTo(5);   // RimHalo, Background, HaloPath, ProgressPath, Symbol
    }

    [StaFact]
    public void AddTo_PlacesHaloBeforeProgressArc()
    {
        // HaloZOrderTests guards the XAML-declared halos; this is the code-built analogue.
        // In WPF later siblings paint on top, so HaloPath must precede ProgressPath or the
        // halo renders as a dark ring over the accent (spec §4.1.3).
        var canvas = new Canvas();
        var btn = DwellArcButton.Create(new Point(10, 10), radius: 20, symbol: "X");
        btn.AddTo(canvas);

        Check.That(canvas.Children.IndexOf(btn.HaloPath))
             .IsStrictlyLessThan(canvas.Children.IndexOf(btn.ProgressPath));
    }

    /// <summary>
    /// The pairing guard for <see cref="DwellArcButton.RemoveFrom"/>. Built with every optional
    /// layer the button can legally carry — an icon with BOTH a fill and a stroke layer, and a
    /// label — so all seven elements exist; a symbol cannot join them (Create refuses a symbol
    /// beside a label, and a symbol-only button would leave a layer untested).
    ///
    /// The removal side is stated as "the canvas is empty afterwards" rather than as a count, so
    /// that it needs no editing when the element set grows. The fixture assertion below IS a
    /// count, and on an eighth element it is what fires first — so its message names RemoveFrom
    /// rather than sending the reader off to fix the fixture and move on.
    /// </summary>
    [StaFact]
    public void RemoveFrom_IsTheInverseOfAddTo()
    {
        var canvas = new Canvas();
        var btn = DwellArcButton.Create(new Point(10, 10), radius: 20, symbol: null,
            icon: ToolbarIcons.For(ToolbarIconKey.SelectionManual), label: "Label");

        btn.AddTo(canvas);
        Check.WithCustomMessage("AddTo added a number of elements this fixture does not expect. "
                + "If you have just added an EIGHTH element to AddTo, this fires before the removal "
                + "check below: add it to RemoveFrom too, then update this count.")
            .That(canvas.Children.Count).IsEqualTo(7);

        btn.RemoveFrom(canvas);

        Check.WithCustomMessage("RemoveFrom left something behind — it has drifted out of step "
                + "with AddTo, which is how a stale layer accumulates over a rebuilt button.")
            .That(canvas.Children.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Removing a button touches only its own visuals. The toolbar removes ONE button from a
    /// canvas that holds five others plus separators and automation anchors.
    /// </summary>
    [StaFact]
    public void RemoveFrom_LeavesOtherChildrenAlone()
    {
        var canvas = new Canvas();
        var keep = DwellArcButton.Create(new Point(100, 10), radius: 20, symbol: "K");
        var drop = DwellArcButton.Create(new Point(10, 10), radius: 20, symbol: "D");
        keep.AddTo(canvas);
        drop.AddTo(canvas);

        drop.RemoveFrom(canvas);

        Check.That(canvas.Children.Count).IsEqualTo(5);
        Check.That(canvas.Children.Contains(keep.Background)).IsTrue();
        Check.That(canvas.Children.Contains(drop.Background)).IsFalse();
    }
}
