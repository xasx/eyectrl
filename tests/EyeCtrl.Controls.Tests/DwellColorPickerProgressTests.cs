namespace EyeCtrl.Controls.Tests;

using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// Colour swatches showed no dwell feedback at all until 2026-08-20. DwellColorPicker assigns
/// its own brush to each swatch's Root.Background, which orphaned the brush DwellButton's
/// ApplyProgress mutated — so the swatch sat inert for the whole dwell and then fired. Nothing
/// threw, so nothing caught it.
///
/// The progress bar is a separate element, immune to a background swap. These tests hold that.
///
/// A swatch is now a card whose own fill is a neutral surface, carrying a chip that holds the
/// preset colour above a caption naming it — so the colour these tests follow through a dwell
/// is the CHIP's, not the card's.
/// </summary>
public class DwellColorPickerProgressTests
{
    [StaFact]
    public void ASwatch_ShowsDwellProgress_DespiteItsBackgroundBeingReplaced()
    {
        // "#A5F3FC" is passed as the sole preset, not just the selection-getter value, so it is
        // genuinely the swatch's paint colour below — not a literal that merely looks like one.
        var hex = "#A5F3FC";
        var registrar = new FakeDwellRegistrar();
        var picker = new DwellColorPicker(
            registrar, "cp", "Colour", () => hex, v => hex = v,
            DwellCategory.Control, 50, 800,
            presets: new[] { (hex, "Test") });

        var swatch = picker.Children.First();
        // DwellColorPicker does assign Root.Background itself (the card surface), but it assigns
        // it once, in the constructor. The test swaps it again here, AFTER construction, so the
        // invariant is pinned as a property of DwellButton — a replaced Background cannot orphan
        // the bar, whoever replaces it and whenever — rather than as a property of what this one
        // caller happens to paint today.
        swatch.Root.Background = new SolidColorBrush(Colors.Magenta);
        registrar.Progress = 0.5;
        swatch.UpdateVisual();

        var bar = FindBar(swatch);
        Check.That(((ScaleTransform)bar.RenderTransform).ScaleX).IsEqualTo(0.5);
    }

    /// <summary>
    /// What the user picks is the colour, so the chip must still be showing it when the dwell
    /// completes. The card around it is a neutral surface and says nothing about the choice.
    /// </summary>
    [StaFact]
    public void ASwatchsChip_KeepsThePresetColour_WhileDwelling()
    {
        // Same as above: "#A5F3FC" is the sole preset, so it really is the chip's paint colour,
        // not merely the selection-getter's initial value.
        var hex = "#A5F3FC";
        var registrar = new FakeDwellRegistrar();
        var picker = new DwellColorPicker(
            registrar, "cp", "Colour", () => hex, v => hex = v,
            DwellCategory.Control, 50, 800,
            presets: new[] { (hex, "Test") });

        var swatch = picker.Children.First();
        var expected = (Color)ColorConverter.ConvertFromString(hex);
        Check.That(((SolidColorBrush)FindChip(swatch).Background).Color).IsEqualTo(expected);

        registrar.Progress = 1.0;
        swatch.UpdateVisual();

        Check.That(((SolidColorBrush)FindChip(swatch).Background).Color).IsEqualTo(expected);
    }

    private static Rectangle FindBar(DwellControl control) =>
        ((DwellButton)control).ProgressBar;

    /// <summary>
    /// The colour chip inside a swatch, reached through <see cref="DwellButton.Content"/> — the
    /// same seam <c>DwellColorPickerLabelTests</c> reads the caption through, so both files
    /// describe the swatch's anatomy the one way rather than through a production accessor added
    /// for tests alone.
    /// </summary>
    /// <param name="control">The swatch to read.</param>
    /// <returns>Its colour chip.</returns>
    private static Border FindChip(DwellControl control) =>
        ((Panel)((DwellButton)control).Content!).Children.OfType<Border>().Single();

}
