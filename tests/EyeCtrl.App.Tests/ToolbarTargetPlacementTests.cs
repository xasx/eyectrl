namespace EyeCtrl.App.Tests;

using System.Windows;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// Guards the rule that made every toolbar button undwellable once: a dwell target must never be
/// published from a button face that has not been through a layout pass.
///
/// <para>
/// An unarranged face reports <c>ActualWidth == 0</c> and carries no visual offset, so
/// <c>PointToScreen(ActualWidth / 2, ActualHeight / 2)</c> degenerates to <c>PointToScreen(0, 0)</c>
/// and returns the SAME screen point for every button. The shipped build created all six dwell
/// targets stacked on that one point; because <c>UpdateButtonScreenPosition</c> creates a target on
/// its first call, that was permanent. The recorded symptom: a toolbar that looked correct and on
/// which no button responded to dwell.
/// </para>
///
/// <para>
/// These run headless. That is not a limitation here — it is the point: the guard has to return
/// BEFORE <c>PointToScreen</c>, which throws without a window source, so a headless run reaching
/// the end of <c>UpdateTargetPositions</c> without throwing is itself evidence of the ordering.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ToolbarTargetPlacementTests
{
    [StaFact]
    public void FreshBand_IsNotArranged_SoNothingIsPublished()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();

        // No layout pass has run: the faces exist but have never been measured or arranged.
        Check.That(window.FacesAreArranged()).IsFalse();

        // The gaze route's entry point. With the guard correctly ordered this returns without
        // publishing and, crucially, without reaching PointToScreen.
        window.UpdateGaze(new PhysicalPoint(100, 100), 0);

        Check.That(window.TargetsInitialized).IsFalse();
    }

    [StaFact]
    public void ArrangedBand_ReportsArranged_AndEveryFaceHasItsFullDiameter()
    {
        // includeReset: true — the id list below asserts ResetAdaptation's face too.
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar(includeReset: true);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();

        Check.That(window.FacesAreArranged()).IsTrue();

        // The distinctness that the collapsed-target bug destroyed: each face occupies its own
        // slot. Canvas.Left is what PointToScreen turns into a distinct screen centre.
        string[] ids = { "Pause", "SelectionMode", "Keyboard", "Settings", "TaskSwitcher", "ResetAdaptation" };
        var lefts = new System.Collections.Generic.List<double>();
        foreach (var id in ids)
        {
            var face = window.ButtonForTest(id).Background;
            Check.That(face.ActualWidth).IsEqualTo(ToolbarTestHelpers.ButtonSize);
            lefts.Add(System.Windows.Controls.Canvas.GetLeft(face));
        }

        Check.That(lefts).ContainsNoDuplicateItem();
    }

    /// <summary>
    /// An empty band must not report itself arranged. Otherwise <c>UpdateTargetPositions</c> would
    /// latch having placed nothing, and the gaze route — which stops delivering once the window
    /// says it is initialised — would never call back.
    /// </summary>
    [StaFact]
    public void EmptyBand_IsNeverArranged()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new EyeCtrl.App.Overlays.ToolbarWindow();   // Initialize() never called

        Check.That(window.FacesAreArranged()).IsFalse();
        Check.That(window.TargetsInitialized).IsFalse();
    }
}
