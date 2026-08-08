namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Pins the four container-size couplings the §8.5 ratio migration created and left unguarded.
///
/// <see cref="RawGeometryLiteralTests"/> proves a glyph size is token-SHAPED. It cannot prove one
/// is CORRECT, and the gap is not narrow: swapping <c>ContainerRatio.Dominant</c> for
/// <c>ContainerRatio.Small</c> at any migrated site passes it silently, and so does letting a
/// container const drift from the XAML attribute it was written to mirror — a
/// <c>CloseButtonSize = 40</c> against the XAML's <c>Width="44"</c> is token-backed, still
/// multiplies cleanly, and simply renders a glyph that no longer fills its button. Nothing else in
/// the suite looks at either.
///
/// So each test here asserts two things about one site: that the const still equals the size the
/// XAML declares, and that the glyph inside it still takes the ratio the design chose for that
/// KIND of container. **No expected value is written as a number.** Every one is derived from the
/// same const and the same <see cref="ContainerRatio"/> member the production code reads, so these
/// tests cannot drift with a deliberate resize — only with a broken coupling, which is the whole
/// point.
///
/// Effective values are read off constructed windows rather than regex-parsed out of the XAML, the
/// way <see cref="CornerRadiusClampTests.TheOneCircleConsumer_IsSquare"/> does: that sees a size
/// arriving from a Style and is immune to attribute reordering.
///
/// **Nothing calls Show().** Constructing a <see cref="Window"/> parses its XAML, which is all
/// these need. This suite must never take over the screen of a user whose only input channel is an
/// eye tracker. The Application-with-tokens setup is shared with
/// <see cref="OverlayResourceResolutionTests"/> and <see cref="CornerRadiusClampTests"/> — see
/// <see cref="WpfApplicationGate"/>, and the collection that serialises them.
/// </summary>
[Collection("WpfApplication")]
public class GlyphSizeCouplingTests
{
    /// <summary>Every logical descendant of an element, depth-first, including nested panels.</summary>
    /// <param name="root">The element to walk from; not itself yielded.</param>
    /// <returns>The descendants.</returns>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    [StaFact]
    public void QuickActionsCloseButton_KeepsItsConstAndItsRatioInStepWithTheXaml()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var window = new QuickActionsGridWindow();
        try
        {
            var button = window.FindName("CloseButton") as Border;
            Check.WithCustomMessage("QuickActionsGridWindow has no Border named CloseButton.")
                .That(button).IsNotNull();

            // Half one: the const mirrors the XAML. CloseButtonSize exists only to be multiplied
            // by a ratio; if the XAML's Width moves and it does not, the glyph is sized for a
            // button that no longer exists.
            Check.WithCustomMessage(
                    "QuickActionsGridWindow.CloseButtonSize has drifted from the Width the XAML "
                    + $"declares on CloseButton. Const says {QuickActionsGridWindow.CloseButtonSize}, "
                    + $"XAML renders {button!.Width}. Change both or neither.")
                .That(button.Width).IsEqualTo(QuickActionsGridWindow.CloseButtonSize);
            Check.WithCustomMessage("CloseButton is no longer square.")
                .That(button.Height).IsEqualTo(QuickActionsGridWindow.CloseButtonSize);

            var glyph = window.FindName("CloseGlyph") as TextBlock;
            Check.WithCustomMessage("QuickActionsGridWindow has no TextBlock named CloseGlyph.")
                .That(glyph).IsNotNull();

            // Half two: the RATIO CHOICE. Dominant is the bucket for a 40-50px chrome affordance
            // whose entire content is one glyph, which is exactly what this button is. Any other
            // ratio is token-backed and passes RawGeometryLiteralTests, so this is the only thing
            // that says Dominant rather than Small or Large.
            Check.WithCustomMessage(
                    "CloseGlyph must be CloseButtonSize x ContainerRatio.Dominant — Dominant is "
                    + "the bucket for a lone glyph filling a 40-50px chrome affordance (spec "
                    + $"§8.5). Expected {QuickActionsGridWindow.CloseButtonSize * ContainerRatio.Dominant} "
                    + $"but found {glyph!.FontSize}.")
                .That(glyph.FontSize)
                .IsEqualTo(QuickActionsGridWindow.CloseButtonSize * ContainerRatio.Dominant);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void QuickActionsTile_KeepsItsConstAndItsRatioInStepWithTheItemTemplate()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var window = new QuickActionsGridWindow();
        try
        {
            var tiles = window.FindName("TilesControl") as ItemsControl;
            Check.WithCustomMessage("QuickActionsGridWindow has no ItemsControl named TilesControl.")
                .That(tiles).IsNotNull();
            Check.WithCustomMessage("TilesControl has no ItemTemplate to inspect.")
                .That(tiles!.ItemTemplate).IsNotNull();

            // Instantiating the DataTemplate reads its EFFECTIVE values without needing a bound
            // item or a laid-out window — the tile Border is never a named element of the window
            // itself, so FindName cannot reach it.
            var tile = tiles.ItemTemplate.LoadContent() as Border;
            Check.WithCustomMessage("The ItemTemplate's root is no longer a Border.")
                .That(tile).IsNotNull();

            Check.WithCustomMessage(
                    "QuickActionsGridWindow.TileSize has drifted from the Width the ItemTemplate "
                    + $"declares. Const says {QuickActionsGridWindow.TileSize}, template renders "
                    + $"{tile!.Width}. TileIconSize is derived from the const, so a drift silently "
                    + "sizes every tile icon for the wrong tile.")
                .That(tile.Width).IsEqualTo(QuickActionsGridWindow.TileSize);
            Check.WithCustomMessage("The tile is no longer square.")
                .That(tile.Height).IsEqualTo(QuickActionsGridWindow.TileSize);

            // The ratio choice, pinned at the const rather than at the TextBlock, because
            // TileIconSize is the member the XAML's {x:Static} names.
            Check.WithCustomMessage(
                    "TileIconSize must be TileSize x ContainerRatio.Small — Small is the bucket "
                    + "for an icon sitting ABOVE other content, which is what the tile icon does "
                    + "(it has the action label under it). Expected "
                    + $"{QuickActionsGridWindow.TileSize * ContainerRatio.Small} but found "
                    + $"{QuickActionsGridWindow.TileIconSize}.")
                .That(QuickActionsGridWindow.TileIconSize)
                .IsEqualTo(QuickActionsGridWindow.TileSize * ContainerRatio.Small);

            // And that the template actually reads it. {x:Static ...TileIconSize} is the one glyph
            // size in the app that reaches XAML through the allow-list's static-property branch;
            // repointing it at a {StaticResource} ramp step would pass every other guard.
            var sizes = Descendants(tile).OfType<TextBlock>().Select(t => t.FontSize).ToList();
            Check.WithCustomMessage(
                    "No TextBlock in the tile template is sized from TileIconSize "
                    + $"({QuickActionsGridWindow.TileIconSize}); found {string.Join(", ", sizes)}. "
                    + "The icon has stopped reading the ratio-derived size.")
                .That(sizes).Contains(QuickActionsGridWindow.TileIconSize);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void EyeCareBannerRingGlyphs_KeepTheirConstAndRatioInStepWithTheXaml()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        // RingSize was a constructor-local const, which put it out of reach of any test at all:
        // nothing tied it to the XAML's Width="64" and nothing recorded that Medium was chosen.
        // Hoisting it to an internal class-level const is what makes this assertable.
        var window = new EyeCareBannerWindow();
        try
        {
            foreach (var (ringName, glyphName) in new[]
                     { ("SnoozeRing", "SnoozeGlyph"), ("StartRing", "StartGlyph") })
            {
                var ring = window.FindName(ringName) as Ellipse;
                Check.WithCustomMessage($"EyeCareBannerWindow has no Ellipse named {ringName}.")
                    .That(ring).IsNotNull();

                // The Ellipse fills its container, so the 64 lives on the Grid that wraps it.
                var box = ring!.Parent as FrameworkElement;
                Check.WithCustomMessage($"{ringName} has no FrameworkElement parent to size it.")
                    .That(box).IsNotNull();

                Check.WithCustomMessage(
                        "EyeCareBannerWindow.RingSize has drifted from the size the XAML declares "
                        + $"around {ringName}. Const says {EyeCareBannerWindow.RingSize}, XAML "
                        + $"renders {box!.Width}x{box.Height}. Both glyph sizes are derived from "
                        + "the const, so a drift sizes them for a ring that is not there.")
                    .That(box.Width).IsEqualTo(EyeCareBannerWindow.RingSize);
                Check.WithCustomMessage($"The {ringName} container is no longer square.")
                    .That(box.Height).IsEqualTo(EyeCareBannerWindow.RingSize);

                var glyph = window.FindName(glyphName) as TextBlock;
                Check.WithCustomMessage($"EyeCareBannerWindow has no TextBlock named {glyphName}.")
                    .That(glyph).IsNotNull();

                // Medium, not Dominant: these rings carry a caption underneath ("snooze" /
                // "start"), so the glyph is mid-weight content in its container rather than the
                // whole of it.
                Check.WithCustomMessage(
                        $"{glyphName} must be RingSize x ContainerRatio.Medium (spec §8.5 records "
                        + "this site at 22/64 = 0.344). Expected "
                        + $"{EyeCareBannerWindow.RingSize * ContainerRatio.Medium} but found "
                        + $"{glyph!.FontSize}.")
                    .That(glyph.FontSize)
                    .IsEqualTo(EyeCareBannerWindow.RingSize * ContainerRatio.Medium);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void DragTrailIcons_KeepTheRepeatedExpressionInStepWithIconSize()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        // DragTrailOverlay cannot read IconSize at its FontSize site: RawGeometryLiteralTests
        // examines an assignment's own value, and `FontSize = IconSize` references no token, so it
        // would be flagged. The expression is therefore REPEATED, and until now a comment saying
        // "must equal IconSize" was the only thing holding the two together — while IconSize/2 is
        // what centres the glyph, so a divergence silently offsets it.
        var window = new DragTrailOverlay();
        try
        {
            var canvas = window.FindName("TrailCanvas") as Canvas;
            Check.WithCustomMessage("DragTrailOverlay has no Canvas named TrailCanvas.")
                .That(canvas).IsNotNull();

            var icons = canvas!.Children.OfType<TextBlock>().ToList();
            Check.WithCustomMessage(
                    "Expected the grab and release glyphs on TrailCanvas; found "
                    + $"{icons.Count} TextBlock(s).")
                .That(icons.Count).IsEqualTo(2);

            foreach (var icon in icons)
                Check.WithCustomMessage(
                        "A drag-trail glyph's FontSize has diverged from IconSize, which is what "
                        + "the centring offsets divide by two — so the glyph is now off-centre. "
                        + $"Expected {DragTrailOverlay.IconSize} but found {icon.FontSize}.")
                    .That(icon.FontSize).IsEqualTo(DragTrailOverlay.IconSize);

            // And the ratio choice behind IconSize. Badge is the bucket for a glyph in a circular
            // badge under about 32px, which the 26px disc is.
            Check.WithCustomMessage(
                    "IconSize must be ButtonDiameter x ContainerRatio.Badge — Badge is the bucket "
                    + "for a glyph in a sub-32px circular badge, where a smaller fraction is "
                    + $"illegible. Expected {DragTrailOverlay.ButtonDiameter * ContainerRatio.Badge} "
                    + $"but found {DragTrailOverlay.IconSize}.")
                .That(DragTrailOverlay.IconSize)
                .IsEqualTo(DragTrailOverlay.ButtonDiameter * ContainerRatio.Badge);
        }
        finally
        {
            window.Close();
        }
    }
}
