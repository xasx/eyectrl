namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views;
using EyeCtrl.Controls;

public class SwatchTargetGeometryTests
{
    // NOT tautological, even though ColorSwatchRadius is defined as ControlBtnRadius: this
    // trips the moment someone re-types a literal (the original 35 px defect) — do not
    // "simplify" it away. A swatch is a gaze target and must never be a smaller, harder
    // target than every other settings control.
    [Fact]
    public void ColorSwatchRadius_MeetsStandardTargetFloor()
    {
        Check.That(SettingsWindow.ColorSwatchRadius)
            .IsGreaterOrEqualThan(SettingsWindow.ControlBtnRadius);
    }

    // Adjacent swatch hit circles (diameter 2 * ColorSwatchRadius) must not overlap in
    // either axis once the WrapPanel lays cells out with the configured gutters.
    // Centre-to-centre = cell size + 2 * margin, which must strictly exceed the diameter.
    [Fact]
    public void AdjacentSwatches_HitCirclesDoNotOverlap()
    {
        var hitDiameter = 2.0 * SettingsWindow.ColorSwatchRadius;

        var horizontalSpacing =
            DwellColorPicker.SwatchWidth + 2 * DwellColorPicker.SwatchHorizontalMargin;
        Check.That(horizontalSpacing).IsStrictlyGreaterThan(hitDiameter);

        var verticalSpacing =
            DwellColorPicker.SwatchHeight + 2 * DwellColorPicker.SwatchVerticalMargin;
        Check.That(verticalSpacing).IsStrictlyGreaterThan(hitDiameter);
    }
}
