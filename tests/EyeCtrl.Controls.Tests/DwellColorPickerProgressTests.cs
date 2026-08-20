namespace EyeCtrl.Controls.Tests;

using System.Linq;
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
/// </summary>
public class DwellColorPickerProgressTests
{
    [StaFact]
    public void ASwatch_ShowsDwellProgress_DespiteItsBackgroundBeingReplaced()
    {
        // "#A5F3FC" is passed as the sole preset, not just the selection-getter value, so it is
        // genuinely the swatch's paint colour below — not a literal that merely looks like one.
        var hex = "#A5F3FC";
        var picker = new DwellColorPicker(
            new FakeRegistrar(), "cp", "Colour", () => hex, v => hex = v,
            DwellCategory.Control, 50, 800,
            presets: new[] { (hex, "Test") });

        var swatch = picker.Children.First();
        // DwellColorPicker no longer does this itself (Task 3 removed it) — the test performs
        // the swap directly so the invariant (a replaced Background cannot orphan the bar) is
        // pinned regardless of whether any caller happens to do it today.
        swatch.Root.Background = new SolidColorBrush(Colors.Magenta);
        swatch.UpdateVisualForTest(0.5);

        var bar = FindBar(swatch);
        Check.That(((ScaleTransform)bar.RenderTransform).ScaleX).IsEqualTo(0.5);
    }

    [StaFact]
    public void ASwatch_KeepsItsOwnColour_WhileDwelling()
    {
        // Same as above: "#A5F3FC" is the sole preset, so it really is the swatch's paint colour,
        // not merely the selection-getter's initial value.
        var hex = "#A5F3FC";
        var picker = new DwellColorPicker(
            new FakeRegistrar(), "cp", "Colour", () => hex, v => hex = v,
            DwellCategory.Control, 50, 800,
            presets: new[] { (hex, "Test") });

        var swatch = picker.Children.First();
        var expected = (Color)ColorConverter.ConvertFromString(hex);
        Check.That(((SolidColorBrush)swatch.Root.Background).Color).IsEqualTo(expected);

        swatch.UpdateVisualForTest(1.0);

        Check.That(((SolidColorBrush)swatch.Root.Background).Color).IsEqualTo(expected);
    }

    private static Rectangle FindBar(DwellControl control) =>
        ((DwellButton)control).ProgressBar;

    private class FakeRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
