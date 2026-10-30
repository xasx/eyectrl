namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// C2: the drop disc is drawn AS its hit circle (drawn = hit, as in B8b), its release dwell is an
/// arc from 12 o'clock that follows the progress, and the arc dims while the pause grace holds it.
/// Construction only — the overlay is never shown, so no window reaches the desktop.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class DragTrailOverlayDropDiscTests
{
    private static readonly PhysicalPoint Centre = new(800, 600);

    [StaTheory]
    [InlineData(1.0, 80.0)]
    [InlineData(2.0, 100.0)]
    [InlineData(2.0, 200.0)]
    public void TheDisc_IsDrawnAtTheHitRadius_CentredOnTheTarget(double scale, double radiusPx)
    {
        WithOverlay(scale, overlay =>
        {
            overlay.Update(Centre, Centre, 0, new DragReleaseDisc(Centre, radiusPx, 0, false));

            var rim = overlay.DropRimForTest;
            var r = radiusPx / scale;
            var (cx, cy) = (Centre.X / scale, Centre.Y / scale);
            Check.WithCustomMessage($"DPI {scale}: the drawn disc is not the r{radiusPx} px hit circle.")
                .That(rim.Width).IsCloseTo(2 * r, 1e-9);
            Check.That(rim.Height).IsCloseTo(2 * r, 1e-9);
            Check.That(Canvas.GetLeft(rim)).IsCloseTo(cx - r, 1e-9);
            Check.That(Canvas.GetTop(rim)).IsCloseTo(cy - r, 1e-9);

            // The ✋ badge rides the top of the disc, inside it and clear of the cursor's ✊.
            var badge = overlay.ReleaseBadgeForTest;
            var badgeTop = Canvas.GetTop(badge);
            Check.That(badgeTop).IsGreaterOrEqualThan(cy - r);
            Check.That(badgeTop + badge.Height).IsLessOrEqualThan(cy - (DragTrailOverlay.IconSize / 2));
        });
    }

    [StaTheory]
    [InlineData(0.25, 1.0, 0.0)]   // 3 o'clock
    [InlineData(0.5, 0.0, 1.0)]    // 6 o'clock
    [InlineData(0.75, -1.0, 0.0)]  // 9 o'clock
    public void TheArc_FollowsTheProgress_FromTwelveOClock(double progress, double ux, double uy)
    {
        WithOverlay(2.0, overlay =>
        {
            const double radiusPx = 100;
            overlay.Update(Centre, Centre, 0, new DragReleaseDisc(Centre, radiusPx, progress, false));

            var arc = overlay.DropArcForTest;
            Check.That(arc.Visibility).IsEqualTo(Visibility.Visible);
            var figure = ((PathGeometry)arc.Data).Figures[0];
            var end = ((ArcSegment)figure.Segments[0]).Point;
            var arcRadius = (radiusPx / 2) - DwellArcButton.DefaultArcInset;
            var (cx, cy) = (Centre.X / 2, Centre.Y / 2);

            Check.That(figure.StartPoint.X).IsCloseTo(cx, 1e-9);
            Check.That(figure.StartPoint.Y).IsCloseTo(cy - arcRadius, 1e-9);
            Check.WithCustomMessage($"progress {progress}: the arc does not end where the dwell is.")
                .That(end.X).IsCloseTo(cx + (ux * arcRadius), 1e-6);
            Check.That(end.Y).IsCloseTo(cy + (uy * arcRadius), 1e-6);
        });
    }

    [StaFact]
    public void TheArc_IsHiddenAtZero_AndDimmedOnlyWhileHeld()
    {
        WithOverlay(2.0, overlay =>
        {
            overlay.Update(Centre, Centre, 0, new DragReleaseDisc(Centre, 100, 0, false));
            Check.That(overlay.DropArcForTest.Visibility).IsEqualTo(Visibility.Collapsed);

            overlay.Update(Centre, Centre, 0, new DragReleaseDisc(Centre, 100, 0.4, true));
            Check.That(overlay.DropArcForTest.Visibility).IsEqualTo(Visibility.Visible);
            Check.WithCustomMessage("a held dwell did not take the shared paused look.")
                .That(overlay.DropArcForTest.Opacity).IsEqualTo(DwellPausedCue.ProgressOpacity);

            overlay.Update(Centre, Centre, 0, new DragReleaseDisc(Centre, 100, 0.45, false));
            Check.That(overlay.DropArcForTest.Opacity).IsEqualTo(1.0);
        });
    }

    private static void WithOverlay(double scale, Action<DragTrailOverlay> body)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(scale, scale);
        var overlay = new DragTrailOverlay();
        try
        {
            body(overlay);
        }
        finally
        {
            overlay.Close();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }
}
