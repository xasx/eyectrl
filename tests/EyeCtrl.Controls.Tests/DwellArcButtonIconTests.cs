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
    public void ASymbolWithALabel_IsRefused()
    {
        // Production's Recapture was exactly this pair (glyph + label, no drawn icon) and the
        // label was drawn across the glyph. The icon/label stack cannot lay the pair out
        // correctly, so it is refused rather than rendered wrong.
        ArgumentException? ex = null;
        try
        {
            DwellArcButton.Create(new Point(0, 0), 50, symbol: "\u21BB", label: "Recapture");
        }
        catch (ArgumentException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("symbol");
    }

    [StaFact]
    public void AnEmptySymbolWithALabel_IsNotRefused()
    {
        // The guard is specifically about a NON-EMPTY symbol: keyboard and manual-selection
        // definitions carry Symbol: string.Empty beside their drawn icon.
        var button = DwellArcButton.Create(new Point(0, 0), 50, symbol: string.Empty, label: "Recapture");

        Check.That(button.Label).IsNotNull();
    }

    [StaFact]
    public void TheIcon_ScalesWithTheButton_AtEverySizeAndScale()
    {
        // Plan 1 shipped a crash that a green suite missed because every layout test ran at DPI
        // 1.0 on one screen size. Nothing here reads DpiHelper - Create works in DIP and the
        // window applies the scale - but that is a claim worth pinning rather than assuming, so
        // this walks the radii the four callers actually use (20 = EdgeButtonBarWindow's default
        // drag-button radius, 50 = its/GlassBackdropWindow's shared button radius, 60 =
        // ToolbarWindow's default half-size at the 120 default Size, 70 = ActionMenuWindow) plus
        // the ones a small screen or a scaled display produces.
        foreach (var radius in new[] { 20.0, 22.0, 50.0, 56.0, 60.0, 70.0, 87.5 })
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

    [StaFact]
    public void SoloIcon_TakesContainerRatioSolo()
    {
        // The icon-only gaze surfaces (modes menu, icon-only edge buttons) opt into Solo; every
        // other caller keeps the Standard default and does not move.
        var button = DwellArcButton.Create(
            new Point(0, 0), 20, symbol: null,
            icon: ActionIcons.For(ActionIcon.DoubleClick),
            iconSize: DwellArcButton.IconSize.Solo);

        var expectedScale = 40 * ContainerRatio.Solo / ActionIcons.GridSize;
        var transform = (ScaleTransform)button.IconStroke!.RenderTransform;

        Check.WithCustomMessage(
                "A Solo icon-only button must draw its icon at diameter x ContainerRatio.Solo.")
             .That(transform.ScaleX).IsCloseTo(expectedScale, 1e-9);
        Check.That(transform.ScaleY).IsCloseTo(expectedScale, 1e-9);
    }

    [StaFact]
    public void SoloWithALabel_IsRefused()
    {
        // Solo names a label-less face: the stack would reserve icon space for a label the ratio
        // was not measured beside, so the pair is refused rather than silently downgraded.
        ArgumentException? ex = null;
        try
        {
            DwellArcButton.Create(
                new Point(0, 0), 50, symbol: string.Empty, label: "Close",
                icon: ActionIcons.For(ActionIcon.Close),
                iconSize: DwellArcButton.IconSize.Solo);
        }
        catch (ArgumentException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("iconSize");
    }

    [StaFact]
    public void AnIconWithAnAccessibleName_AddsASizedAutomationAnchor()
    {
        // The drawn Path has no automation peer, so an icon-only button's UIA name needs a
        // peer-bearing element: a hidden TextBlock sized to the disc (ToolbarBand's pattern).
        var centre = new Point(30, 40);
        var button = DwellArcButton.Create(
            centre, 25, symbol: null, icon: ActionIcons.For(ActionIcon.Close),
            accessibleName: "Close");

        Check.That(button.AutomationAnchor).IsNotNull();
        Check.That(System.Windows.Automation.AutomationProperties.GetName(button.AutomationAnchor!))
             .IsEqualTo("Close");
        Check.WithCustomMessage("The anchor's rect must be the disc's, not a text sliver.")
             .That(button.AutomationAnchor!.Width).IsEqualTo(50);
        Check.That(button.AutomationAnchor!.Height).IsEqualTo(50);
        Check.That(Canvas.GetLeft(button.AutomationAnchor)).IsEqualTo(5);
        Check.That(Canvas.GetTop(button.AutomationAnchor)).IsEqualTo(15);
        Check.That(button.AutomationAnchor.IsHitTestVisible).IsFalse();
    }

    [StaFact]
    public void AnIconWithNoAccessibleName_HasNoAnchor()
    {
        // No name, no anchor: the element set only grows where UIA actually needs it.
        var button = DwellArcButton.Create(
            new Point(0, 0), 20, symbol: null, icon: ActionIcons.For(ActionIcon.Close));

        Check.That(button.AutomationAnchor).IsNull();
    }
}
