namespace EyeCtrl.Controls.Tests;

using System.Windows;
using EyeCtrl.Controls;
using NFluent;
using Xunit;

/// <summary>
/// The progress arc's width, which nothing pinned before 2026-08-26.
///
/// <para>
/// That gap had already cost something. <c>DefaultArcThickness</c> was a flat 4 DIP, chosen when
/// every caller was 100 DIP across, so the arc was 4% of the button. Plan 1 grew the action-menu
/// button to 140 DIP and left the constant alone, which quietly thinned that ring's arc to 2.9%
/// of its button while the other callers kept 4%. A full green suite and green CI both missed it
/// because no test looked at the number at all.
/// </para>
///
/// <para>
/// So the weight is now a fraction of the diameter with a floor, and these tests pin both halves
/// at the three diameters that actually ship. The floor is the point: a pure fraction would take
/// the edge bar's 40 DIP drag buttons down to 1.6 DIP, on the affordance that CANCELS A DRAG for
/// a user whose only input channel is an eye tracker.
/// </para>
///
/// <c>[StaFact]</c> because Create builds WPF shapes. No window is created.
/// </summary>
public class DwellArcButtonArcWeightTests
{
    /// <summary>UIConstants.DragButtonRadius — the edge bar's drag/back buttons, 40 DIP across.</summary>
    private const double DragButtonRadius = 20;

    /// <summary>UIConstants.ButtonRadius — the backdrop hide button, 100 DIP across.</summary>
    private const double HideButtonRadius = 50;

    /// <summary>LayoutConstants.ActionMenuButtonRadius — the ring, 140 DIP across since plan 1.</summary>
    private const double ActionMenuRadius = 70;

    [StaFact]
    public void TheActionMenuArc_IsFourPercentOfItsButton()
    {
        var button = DwellArcButton.Create(new Point(0, 0), ActionMenuRadius, symbol: null);

        // 140 x 0.04 = 5.6, restoring the weight the arc had at 100 DIP.
        Check.That(button.ProgressPath.StrokeThickness)
             .IsCloseTo(ActionMenuRadius * 2 * DwellArcButton.ArcThicknessRatio, 1e-9);
        Check.WithCustomMessage("A 140 DIP button must not still draw the flat 4 DIP arc.")
             .That(button.ProgressPath.StrokeThickness).IsStrictlyGreaterThan(4.0);
    }

    [StaFact]
    public void TheSmallerCallers_KeepExactlyWhatTheyHadBefore()
    {
        // Both were 4 DIP under the flat constant and must stay there: the fraction alone would
        // give the 40 DIP button 1.6 DIP, and the floor is what stops that.
        foreach (var radius in new[] { DragButtonRadius, HideButtonRadius })
        {
            var button = DwellArcButton.Create(new Point(0, 0), radius, symbol: null);

            Check.WithCustomMessage($"radius {radius} arc changed weight")
                 .That(button.ProgressPath.StrokeThickness).IsCloseTo(4.0, 1e-9);
        }
    }

    [StaFact]
    public void TheArcNeverThins_AsTheButtonShrinks()
    {
        // The floor holds at every size below it, including the small-screen and scaled-display
        // radii that the suite's 1920x1080-at-DPI-1.0 habit never exercises.
        foreach (var radius in new[] { 12.0, 16.0, 20.0, 25.0, 35.0, 50.0, 56.0, 70.0, 87.5 })
        {
            var button = DwellArcButton.Create(new Point(0, 0), radius, symbol: null);

            Check.WithCustomMessage($"radius {radius} arc fell below the floor")
                 .That(button.ProgressPath.StrokeThickness)
                 .IsGreaterOrEqualThan(DwellArcButton.MinArcThickness);
            Check.WithCustomMessage($"radius {radius} arc is wider than its own button")
                 .That(button.ProgressPath.StrokeThickness).IsStrictlyLessThan(radius);
        }
    }

    [StaFact]
    public void AnExplicitThickness_StillWins()
    {
        // The parameter went from a defaulted double to a nullable one so the default could read
        // the diameter. A caller that passes a number must still get that number.
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null, arcThickness: 9);

        Check.That(button.ProgressPath.StrokeThickness).IsEqualTo(9.0);
    }

    [StaFact]
    public void TheHaloUnderStroke_StaysWiderThanTheArcItBacks()
    {
        // The halo is what gives the arc its contrast guarantee over desktop content (spec
        // 4.1.3). If it stopped tracking the arc's width the arc would paint over its own halo.
        foreach (var radius in new[] { DragButtonRadius, HideButtonRadius, ActionMenuRadius })
        {
            var button = DwellArcButton.Create(new Point(0, 0), radius, symbol: null);

            Check.WithCustomMessage($"radius {radius} halo no longer backs the arc")
                 .That(button.HaloPath.StrokeThickness)
                 .IsStrictlyGreaterThan(button.ProgressPath.StrokeThickness);
        }
    }
}
