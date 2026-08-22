namespace EyeCtrl.Controls.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// Swatch names used to live only in Root.ToolTip. A tooltip needs hover; this application's
/// user has no hover — gaze is their only input channel — so the four presets were four
/// unlabelled rectangles. The name must be drawn.
///
/// <para>
/// The swatch is therefore a card (the button's own fill) carrying a colour chip above a
/// caption. <c>ToolTip</c> is still set, as a bonus for whoever drives this with a mouse, and
/// is explicitly NOT the affordance under test: a test that read <c>Root.ToolTip</c> would have
/// passed on the old, defective code. Every assertion here reads the DRAWN content instead —
/// <see cref="TheName_IsDrawnInsideTheSwatch_NotOnlyInATooltip"/> walks the swatch's own visual
/// subtree, which a tooltip is not part of.
/// </para>
/// <para>
/// Contrast is checked here for the ambient theme only. The cross-theme half is already
/// discharged by <c>SliderPlateContrastTests.TheEndLabels_AreLegibleOnThePlate</c>, which gates
/// exactly this pair — <c>TextSecondary</c> on <c>SurfaceMedium</c> — in every registered
/// theme; duplicating that sweep here would add a second place to keep in step and no coverage.
/// What this file adds is the half a token sweep cannot see: that the control actually reaches
/// for those two tokens.
/// </para>
/// </summary>
public class DwellColorPickerLabelTests
{
    /// <summary>
    /// The 4.5:1 body-text floor. The caption is <c>FontSizeSubtitle</c> (18 DIP) at normal
    /// weight, and WCAG's large-text relaxation to 3:1 starts at 24 DIP, or 18.66 DIP bold — so
    /// it does not qualify and the full ratio applies.
    /// </summary>
    private const double TextRatioFloor = 4.5;

    /// <summary>
    /// The presets these tests drive the picker with, and the source of every expected name
    /// below.
    ///
    /// Explicit rather than <c>DefaultPresets</c>: expected values must not come out of the
    /// class under test, and <see cref="DwellColorPicker.PresetsFor"/> is part of it — a name
    /// misspelt there would be echoed by the expectation and pass. Whether the palette offers
    /// the right four colours is that method's own business, gated by
    /// <c>ColorPickerPresetTests</c>; what this file owns is that whatever name a preset
    /// carries is DRAWN. The names are deliberately not the shipped ones, so no assertion can
    /// coincide with them by accident.
    /// </summary>
    private static readonly (string Hex, string Name)[] Presets =
    [
        ("#A5F3FC", "Ice"),
        ("#7BE495", "Mint"),
        ("#FFD166", "Honey"),
        ("#D6DEF5", "Paper"),
    ];

    private static DwellColorPicker Build()
    {
        var hex = Presets[0].Hex;
        return new DwellColorPicker(
            new FakeRegistrar(), "cp", "Circle Colour", () => hex, v => hex = v,
            DwellCategory.Control, 50, 800, presets: Presets);
    }

    /// <summary>
    /// A picker over two explicit presets whose selection is fixed at
    /// <paramref name="selected"/>, so "which swatch is selected" is an input rather than
    /// something read back off the control.
    /// </summary>
    /// <param name="selected">Hex of the preset the getter reports as current.</param>
    /// <returns>The picker.</returns>
    private static DwellColorPicker BuildWithSelection(string selected) =>
        new(new FakeRegistrar(), "cp", "Circle Colour", () => selected, _ => { },
            DwellCategory.Control, 50, 800,
            presets: new[] { ("#A5F3FC", "One"), ("#FFD166", "Two") });

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Of(Brush brush) => ((SolidColorBrush)brush).Color;

    /// <summary>
    /// Every <see cref="TextBlock"/> in <paramref name="root"/>'s visual subtree, found by
    /// walking the tree rather than by reading any single known property.
    ///
    /// A <c>ToolTip</c> is not part of this subtree — it is a separate popup, built on demand —
    /// so a name that lives only in a tooltip is invisible to this walk. That asymmetry is the
    /// point: it is what makes the caption's presence here evidence of a hover-free affordance.
    /// </summary>
    /// <param name="root">Subtree root.</param>
    /// <returns>The text blocks found, in visual order.</returns>
    private static IEnumerable<TextBlock> TextBlocksUnder(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
                yield return text;

            foreach (var nested in TextBlocksUnder(child))
                yield return nested;
        }
    }

    /// <summary>
    /// The caption <see cref="TextBlock"/> inside <paramref name="swatch"/>'s content.
    ///
    /// Asserts its way down rather than indexing: a caller that finds no caption gets a sentence
    /// saying so, not an <c>InvalidOperationException</c> from inside a projection. The sibling
    /// tests explain what a missing caption MEANS, but a reader running this file's contrast
    /// test alone should still learn what happened.
    /// </summary>
    /// <param name="swatch">The swatch to read.</param>
    /// <returns>Its single caption.</returns>
    private static TextBlock CaptionOf(DwellControl swatch)
    {
        var content = ((DwellButton)swatch).Content as Panel;
        Check.WithCustomMessage(
                "the swatch's DwellButton.Content is not a Panel, so it holds no caption to "
                + "measure — see EverySwatch_DrawsItsName.")
            .That(content).IsNotNull();

        var caption = content!.Children.OfType<TextBlock>().SingleOrDefault();
        Check.WithCustomMessage(
                "the swatch draws no single caption, so there is no text whose contrast could be "
                + "measured — see EverySwatch_DrawsItsName.")
            .That(caption).IsNotNull();

        return caption!;
    }

    [StaFact]
    public void EverySwatch_DrawsItsName()
    {
        var picker = Build();
        var expected = Presets.Select(p => p.Name).ToList();

        // SelectMany rather than Single(): a swatch that draws no caption must make the count
        // check below SPEAK, not throw "Sequence contains no elements" from inside a projection.
        // A swatch drawing two captions overshoots the count instead, so "exactly one" is still
        // pinned — by an assertion rather than by an exception.
        var drawn = picker.Children
            .Select(c => ((DwellButton)c).Content)
            .OfType<Panel>()
            .SelectMany(p => p.Children.OfType<TextBlock>().Select(t => t.Text))
            .ToList();

        // Asserted separately from the comparison below so a collapsed projection reports
        // itself as one: if Content is not a Panel, or carries no TextBlock, the projection
        // comes back short and this says which stage lost the name rather than printing two
        // unequal lists.
        Check.WithCustomMessage(
                $"{expected.Count} presets are offered but only {drawn.Count} drawn names were "
                + "found. Each swatch's DwellButton.Content must be a Panel containing exactly "
                + "one TextBlock — the caption. A name that reaches only Root.ToolTip is "
                + "unreachable for a user with no hover.")
            .That(drawn.Count).IsEqualTo(expected.Count);

        // ContainsExactly is order-sensitive in NFluent (which is why
        // DwellProgressContrastTests.KnownDwellControlSubclasses_MatchTheTestedSet sorts before
        // calling it), so this pins name-to-swatch pairing and not merely the set of names.
        Check.That(drawn).ContainsExactly(expected);
    }

    [StaFact]
    public void EverySwatch_KeepsTheStandardTargetGeometry()
    {
        var picker = Build();

        foreach (var swatch in picker.Children)
        {
            Check.That(swatch.Root.Width).IsEqualTo(DwellColorPicker.SwatchWidth);
            Check.That(swatch.Root.Height).IsEqualTo(DwellColorPicker.SwatchHeight);
        }
    }

    /// <summary>
    /// The defect was never "the name is missing" — it was "the name exists, on hover". So this
    /// asserts the name is inside the swatch's own drawn subtree, reachable with no pointer at
    /// all. See <see cref="TextBlocksUnder"/> for why a tooltip cannot satisfy this walk.
    /// </summary>
    [StaFact]
    public void TheName_IsDrawnInsideTheSwatch_NotOnlyInATooltip()
    {
        var picker = Build();
        var names = Presets.Select(p => p.Name).ToList();

        var swatches = picker.Children.ToList();
        Check.That(swatches.Count).IsEqualTo(names.Count);

        for (var i = 0; i < swatches.Count; i++)
        {
            var drawn = TextBlocksUnder(swatches[i].Root).Select(t => t.Text).ToList();
            Check.WithCustomMessage(
                    $"swatch {i} draws {(drawn.Count == 0 ? "nothing" : string.Join("/", drawn))} "
                    + $"inside itself, so its name '{names[i]}' is reachable only by hovering — "
                    + "which this application's user cannot do.")
                .That(drawn).Contains(names[i]);
        }
    }

    /// <summary>
    /// Legible, not merely present. The caption is drawn on the button's own fill — the card —
    /// so that is the surface it is measured against, read off the control rather than assumed.
    /// </summary>
    [StaFact]
    public void TheName_IsLegibleOnTheCardItIsDrawnOn()
    {
        var picker = Build();

        foreach (var swatch in picker.Children)
        {
            var caption = CaptionOf(swatch);
            var card = Of(swatch.Root.Background);
            var ratio = Contrast(Of(caption.Foreground), card);

            Check.WithCustomMessage(
                    $"the swatch caption '{caption.Text}' reaches only {ratio:F2}:1 against the "
                    + $"card it is drawn on ({card}). At {Tokens.FontSizeSubtitle} DIP normal "
                    + $"weight it is not WCAG large text, so it must clear {TextRatioFloor}:1. "
                    + "TextDim was tried for this pair elsewhere and is 4.37:1 in Midnight — do "
                    + "not go back to it.")
                .That(ratio).IsGreaterOrEqualThan(TextRatioFloor);
        }
    }

    /// <summary>
    /// A <see cref="Border"/>'s thickness lays out OUTSIDE its child, so a thickness that grew
    /// with selection would move the swatch and re-flow the whole WrapPanel every time the user
    /// picked a colour — the same rule <c>DwellComboBox</c>'s <c>OptionBorderThickness</c>
    /// states for its option pills. Two pickers with different selections make the comparison
    /// real: only the brush may differ between them.
    /// </summary>
    [StaFact]
    public void SelectingASwatch_ChangesOnlyItsBorderBrush_NeverItsThickness()
    {
        var first = BuildWithSelection("#A5F3FC").Children.ToList();
        var second = BuildWithSelection("#FFD166").Children.ToList();

        var expected = new Thickness(DwellColorPicker.SwatchBorderThickness);
        foreach (var (swatch, where) in first.Select((s, i) => (s, $"first picker, swatch {i}"))
                     .Concat(second.Select((s, i) => (s, $"second picker, swatch {i}"))))
        {
            Check.WithCustomMessage(
                    $"{where} reports BorderThickness {swatch.Root.BorderThickness} rather than "
                    + $"{expected}. A selection-dependent thickness re-flows the WrapPanel and "
                    + "moves every gaze target on the row.")
                .That(swatch.Root.BorderThickness).IsEqualTo(expected);
        }

        // The brush is the channel selection is allowed to use, and it must actually be used —
        // otherwise "only the brush differs" would be satisfied by nothing differing at all.
        Check.WithCustomMessage(
                "swatch 0 paints the same border brush whether or not it is the selected colour, "
                + "so selection is invisible.")
            .That(Of(first[0].Root.BorderBrush)).IsNotEqualTo(Of(second[0].Root.BorderBrush));
        Check.WithCustomMessage(
                "swatch 1 paints the same border brush whether or not it is the selected colour, "
                + "so selection is invisible.")
            .That(Of(first[1].Root.BorderBrush)).IsNotEqualTo(Of(second[1].Root.BorderBrush));
    }

    private class FakeRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
