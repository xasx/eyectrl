namespace EyeCtrl.Controls.Tests;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// Covers the icon path added to <c>DwellArcButton.Create</c> on 2026-08-26.
///
/// <c>impact({target: "DwellArcButton", direction: "upstream"})</c> reports HIGH risk across two
/// execution flows and three call sites, only one of which is the action menu. The first test
/// here is therefore the important one: it pins that the glyph path is untouched, which is what
/// lets the other call sites go unedited.
///
/// <c>[StaFact]</c> because Create builds WPF shapes. No window is created and nothing is shown.
/// </summary>
public class DwellArcButtonIconTests
{
    private const double ActionMenuRadius = 70;

    [StaFact]
    public void WithNoIcon_TheGlyphPathIsUnchanged()
    {
        var button = DwellArcButton.Create(new Point(0, 0), 50, "X");

        Check.WithCustomMessage("The glyph caller lost its TextBlock.")
             .That(button.Symbol).IsNotNull();
        Check.WithCustomMessage("A glyph caller must not gain icon paths.")
             .That(button.IconStroke).IsNull();
        Check.That(button.IconFill).IsNull();
    }

    [StaFact]
    public void WithAnIcon_BothLayersAreBuilt()
    {
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.LeftClick));

        Check.WithCustomMessage("LeftClick has a stroke layer.").That(button.IconStroke).IsNotNull();
        Check.WithCustomMessage("LeftClick has a fill layer.").That(button.IconFill).IsNotNull();
        Check.WithCustomMessage("An icon caller must not also get a TextBlock.")
             .That(button.Symbol).IsNull();
    }

    [StaFact]
    public void AStrokeOnlyIcon_BuildsOnlyTheStrokeLayer()
    {
        // Back is stroke-only; this asserts the null layer is skipped rather than rendered empty.
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.Back));

        Check.That(button.IconStroke).IsNotNull();
        Check.That(button.IconFill).IsNull();
    }

    [StaFact]
    public void TheIcon_TakesContainerRatioDominant()
    {
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.LeftClick));

        var dia = ActionMenuRadius * 2;
        var expectedScale = dia * ContainerRatio.Dominant / ActionIcons.GridSize;
        var transform = (ScaleTransform)button.IconStroke!.RenderTransform;

        Check.WithCustomMessage(
                $"A {dia}px button must draw its icon at {dia * ContainerRatio.Dominant}px - "
                + "ContainerRatio.Dominant is the token for an affordance whose entire content is "
                + "one glyph with no label beside it, which is the resting action-menu button.")
            .That(transform.ScaleX).IsCloseTo(expectedScale, 1e-9);
        Check.That(transform.ScaleY).IsCloseTo(expectedScale, 1e-9);
    }

    [StaFact]
    public void TheIcon_IsCentredOnTheButton()
    {
        var centre = new Point(300, 200);
        var button = DwellArcButton.Create(
            centre, ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.LeftClick));

        var iconSize = ActionMenuRadius * 2 * ContainerRatio.Dominant;

        Check.That(Canvas.GetLeft(button.IconStroke!)).IsCloseTo(centre.X - iconSize / 2, 1e-9);
        Check.That(Canvas.GetTop(button.IconStroke!)).IsCloseTo(centre.Y - iconSize / 2, 1e-9);
    }

    [StaFact]
    public void TheIcon_UsesRoundLineCaps()
    {
        // ToolbarIcons.Keyboard draws its four key dots as zero-length segments that render as
        // dots only under a round cap. Nothing asserted that dependency before; this pins it here,
        // against the actual Keyboard icon, so a future change to Flat caps fails a test instead
        // of silently erasing those dots.
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null,
            icon: ToolbarIcons.For(ToolbarIconKey.Keyboard));

        Check.That(button.IconStroke!.StrokeStartLineCap).IsEqualTo(PenLineCap.Round);
        Check.That(button.IconStroke!.StrokeEndLineCap).IsEqualTo(PenLineCap.Round);
    }

    [StaFact]
    public void TheIcon_TakesItsColourFromTheTheme()
    {
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.LeftClick));

        // The glyphs this replaced included one colour emoji that ignored the palette entirely.
        Check.That(button.IconStroke!.Stroke).IsSameReferenceAs(Tokens.BrushTextPrimary);
        Check.That(button.IconFill!.Fill).IsSameReferenceAs(Tokens.BrushTextPrimary);
    }

    [StaFact]
    public void TheFillLayer_IsAddedBeforeTheStrokeLayer()
    {
        // A filled layer painted over its own outline erases it. Nothing else in the repo guards
        // this: HaloZOrderTests reads x:Name order in XAML files and names no .cs file, and
        // DwellArcButtonTests.AddTo_AddsAllVisualsToCanvas builds a glyph button, so it passes at
        // any icon ordering.
        var canvas = new Canvas();
        var button = DwellArcButton.Create(
            new Point(0, 0), ActionMenuRadius, symbol: null,
            icon: ActionIcons.For(ActionIcon.LeftClick));
        button.AddTo(canvas);

        Check.That(canvas.Children.IndexOf(button.IconFill))
             .IsStrictlyLessThan(canvas.Children.IndexOf(button.IconStroke));
    }

    [StaFact]
    public void TheIcon_ScalesWithTheButton_AtEverySizeAndScale()
    {
        // Plan 1 shipped a crash that a green suite missed because every layout test ran at DPI
        // 1.0 on one screen size. Nothing here reads DpiHelper - Create works in DIP and the
        // window applies the scale - but that is a claim worth pinning rather than assuming, so
        // this walks the radii the three callers actually use plus the ones a small screen or a
        // scaled display produces.
        foreach (var radius in new[] { 20.0, 22.0, 50.0, 56.0, 70.0, 87.5 })
        {
            var button = DwellArcButton.Create(
                new Point(0, 0), radius, symbol: null,
                icon: ActionIcons.For(ActionIcon.ScrollMode));

            var expected = radius * 2 * ContainerRatio.Dominant / ActionIcons.GridSize;
            var transform = (ScaleTransform)button.IconStroke!.RenderTransform;

            Check.WithCustomMessage($"radius {radius} icon scale")
                 .That(transform.ScaleX).IsCloseTo(expected, 1e-9);
            Check.WithCustomMessage($"radius {radius} icon must stay inside the button")
                 .That(radius * 2 * ContainerRatio.Dominant).IsStrictlyLessThan(radius * 2);
        }
    }
}
