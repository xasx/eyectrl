namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Services;
using Xunit;

/// <summary>
/// Constructs the real <c>ManualSelectionGazeIndicatorWindow</c> (WPF <c>Window</c> with XAML) and
/// pins part (d) of the manual-selection targeting unit: the held anchor is drawn as its own mark,
/// placed at the anchor's screen point, and it does not move when the gaze dot does.
///
/// <para>
/// The window half of the mark cannot be asserted from the service or view model tests, and it is
/// exactly the seam that a green suite would otherwise leave unpinned: without the mark, a drifting
/// commit is indistinguishable from having aimed badly, which is the reportability failure the
/// debate called non-negotiable.
/// </para>
///
/// <para>
/// Sits in the DpiHelper collection because it writes the process-global scale (the guard's rule
/// for writers) and calls the WPF application gate; both collections are DisableParallelization, so
/// this never overlaps another window-constructing class. The scale is restored in a finally, the
/// obligation the collection docs put on every writer.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ManualSelectionGazeIndicatorWindowTests
{
    [StaFact]
    public void AnchorMark_IsPlacedAtTheAnchorScreenPoint_AndStaysThereAsTheGazeMoves()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(1.0, 1.0);
        try
        {
            var window = new ManualSelectionGazeIndicatorWindow();
            window.Configure(outerDiameter: 60, centerDotDiameter: 10,
                strokeThickness: 2, opacity: 0.7, holdRadiusLogical: 72);

            // The window must cover a fully-drifted anchor, or the mark would be clipped away.
            Check.That(window.Width).IsCloseTo(60 + 2 * 72, 0.001);

            var mark = (FrameworkElement)window.FindName("AnchorMark");

            // Before the anchor seats there is no mark.
            Check.That(mark.Visibility).IsEqualTo(Visibility.Collapsed);

            window.UpdatePosition(1000, 500);
            window.UpdateAnchor(1072, 500, visible: true);

            Check.That(mark.Visibility).IsEqualTo(Visibility.Visible);

            // The anchor is 72 px right of the gaze: window centre (102) + 72, minus half the 24 DIP mark.
            Check.That(Canvas.GetLeft(mark)).IsCloseTo(102 + 72 - 12, 0.001);
            Check.That(Canvas.GetTop(mark)).IsCloseTo(102 - 12, 0.001);

            // The gaze moves, the anchor does not: the mark must move the other way in window space.
            window.UpdatePosition(960, 500);
            Check.That(Canvas.GetLeft(mark)).IsCloseTo(102 + (1072 - 960) - 12, 0.001);

            window.UpdateAnchor(1072, 500, visible: false);
            Check.That(mark.Visibility).IsEqualTo(Visibility.Collapsed);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }
}
