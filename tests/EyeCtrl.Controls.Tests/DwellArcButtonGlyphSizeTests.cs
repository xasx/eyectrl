namespace EyeCtrl.Controls.Tests;

using System.Windows;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Pins BOTH arms of <c>DwellArcButton</c>'s size-conditional ratio choice.
///
/// The symbol's size is <c>dia * (dia &lt; BadgeDiameterThreshold ? Badge : Small)</c>. That
/// branch is the whole reason the 40px edge-bar drag buttons are legible: under
/// <see cref="ContainerRatio.Small"/> they would render at 9.2px, on the affordance that cancels a
/// drag for a user whose only input channel is an eye tracker. Nothing guarded it — the expression
/// is token-backed either way, so <c>RawGeometryLiteralTests</c> passes if the threshold moves, if
/// the arms are swapped, or if the branch collapses to one ratio. A <c>Math.Max</c> against the
/// 12px floor would also pass that guard and would give these glyphs 12px instead of 24px, which
/// is exactly the outcome the threshold was chosen over.
///
/// Both radii below are the REAL caller radii, not samples: 20 is
/// <c>UIConstants.DragButtonRadius</c> (the default on <c>EdgeButtonDefinition</c>, used by
/// <c>Drag_cancel</c> / <c>Drag_back</c>) and 50 is <c>UIConstants.ButtonRadius</c> /
/// <c>ActionMenuGeometry.ButtonRadius</c> (the action menu and hide button). They are written here
/// rather than referenced because those consts live in EyeCtrl.ViewModels, which this project does
/// not reference. Each expected size is derived from the diameter and the ratio the production
/// code reads, never written as a number, so a deliberate resize cannot make these stale — only a
/// broken branch can.
///
/// <c>[StaFact]</c> because <c>Create</c> builds WPF shapes and calls <c>Measure</c> on the
/// TextBlock. No window is created and nothing is shown.
/// </summary>
public class DwellArcButtonGlyphSizeTests
{
    /// <summary>UIConstants.DragButtonRadius — the edge bar's drag/back buttons.</summary>
    private const double SmallCallerRadius = 20;

    /// <summary>
    /// UIConstants.ButtonRadius — the backdrop hide button and ManualSelection side buttons.
    /// NOT the action menu: that split onto LayoutConstants.ActionMenuButtonRadius (70) on
    /// 2026-08-26. Either radius exercises the same above-threshold arm, so 50 still covers it.
    /// </summary>
    private const double LargeCallerRadius = 50;

    [StaFact]
    public void BelowTheThreshold_TheGlyphTakesBadge()
    {
        var dia = SmallCallerRadius * 2;
        var button = DwellArcButton.Create(new Point(0, 0), SmallCallerRadius, "✊");

        Check.WithCustomMessage("Create returned no Symbol for a non-null glyph.")
            .That(button.Symbol).IsNotNull();
        Check.WithCustomMessage(
                $"A {dia}px-diameter DwellArcButton must take ContainerRatio.Badge, not Small. "
                + $"Expected {dia * ContainerRatio.Badge} but found {button.Symbol!.FontSize}. "
                + "Under Small this renders at 9.2px, and it is the drag-cancel affordance. Do "
                + "not 'fix' this with Math.Max against the 12px floor — that gives 12px, which "
                + "is what BadgeDiameterThreshold was chosen over.")
            .That(button.Symbol.FontSize).IsEqualTo(dia * ContainerRatio.Badge);
    }

    [StaFact]
    public void AtOrAboveTheThreshold_TheGlyphTakesSmall()
    {
        var dia = LargeCallerRadius * 2;
        var button = DwellArcButton.Create(new Point(0, 0), LargeCallerRadius, "☝");

        Check.WithCustomMessage("Create returned no Symbol for a non-null glyph.")
            .That(button.Symbol).IsNotNull();
        Check.WithCustomMessage(
                $"A {dia}px-diameter DwellArcButton must keep ContainerRatio.Small — that is the "
                + "ratio these callers already observed (22/100 = 0.22), and Badge here would "
                + $"oversize the action-menu glyphs by half. Expected {dia * ContainerRatio.Small} "
                + $"but found {button.Symbol!.FontSize}.")
            .That(button.Symbol.FontSize).IsEqualTo(dia * ContainerRatio.Small);
    }

    [StaFact]
    public void NeitherArm_TakesTheOtherArmsRatio_SoTheBranchCannotHaveCollapsed()
    {
        // The two assertions above pin what each arm DOES take. This pins what it must not, which
        // is what catches the branch collapsing onto one ratio in either direction, or the two arms
        // being swapped.
        //
        // Stated as two exact inequalities rather than by comparing the two fractions. A fraction
        // comparison was tried and MEASURED NOT TO BITE: with both arms on Small the fractions are
        // 9.200000000000001/40 and 23/100, which differ only in floating-point noise, and the
        // noise happened to fall the way the assertion wanted — the test passed on a branch that
        // had genuinely collapsed. Exact equality against a value computed the same way the
        // production code computes it has no such slack.
        var smallDia = SmallCallerRadius * 2;
        var largeDia = LargeCallerRadius * 2;
        var small = DwellArcButton.Create(new Point(0, 0), SmallCallerRadius, "✊");
        var large = DwellArcButton.Create(new Point(0, 0), LargeCallerRadius, "☝");

        Check.WithCustomMessage(
                "The sub-threshold caller is on ContainerRatio.Small — the branch has collapsed "
                + $"onto one ratio, or the arms are swapped. Its glyph is {small.Symbol!.FontSize}, "
                + $"which is exactly {smallDia} x Small. The 40px drag buttons are back at 9.2px.")
            .That(small.Symbol.FontSize).Not.IsEqualTo(smallDia * ContainerRatio.Small);

        Check.WithCustomMessage(
                "The above-threshold caller is on ContainerRatio.Badge — the branch has collapsed "
                + $"the other way. Its glyph is {large.Symbol!.FontSize}, which is exactly "
                + $"{largeDia} x Badge, half again the size the action menu was designed at.")
            .That(large.Symbol.FontSize).Not.IsEqualTo(largeDia * ContainerRatio.Badge);
    }

    [StaFact]
    public void AnExplicitFontSize_StillOverridesBothArms()
    {
        // The branch is only the DEFAULT. Callers that pass fontSize must be unaffected by it, or
        // the threshold would silently resize every explicit caller too.
        var explicitSize = Legibility.MinimumGlyphFontSize;
        var button = DwellArcButton.Create(
            new Point(0, 0), SmallCallerRadius, "✊", fontSize: explicitSize);

        Check.WithCustomMessage("An explicit fontSize must win over the size-conditional default.")
            .That(button.Symbol!.FontSize).IsEqualTo(explicitSize);
    }
}
