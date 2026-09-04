namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class CursorRingStacksTests
{
    private const string Mark = "#A5F3FC";
    private const string Halo = "#0A0A14";
    private const string Light = "#D6DEF5";

    private static CursorRingStack Build(CursorRingStyle style, double size = 250, double thickness = 3, double opacity = 1.0) =>
        CursorRingStacks.For(style, size, thickness, opacity, Mark, Halo, Light);

    [Fact]
    public void DefaultRingStyle_IsWeightedSandwich()
    {
        Check.That(new CursorSettings().RingStyle).IsEqualTo(CursorRingStyle.WeightedSandwich);
    }

    [Fact]
    public void WeightedSandwich_HaloOutlineIsFourWiderThanTheAccentCore()
    {
        var stack = Build(CursorRingStyle.WeightedSandwich);

        Check.That(stack.Outer.Hex).IsEqualTo(Halo);
        Check.That(stack.Outer.Thickness).IsEqualTo(7.0);
        Check.That(stack.Outer.Opacity).IsEqualTo(1.0);

        Check.That(stack.Core.Hex).IsEqualTo(Mark);
        Check.That(stack.Core.Thickness).IsEqualTo(3.0);
    }

    [Fact]
    public void WeightedSandwich_CoreCarriesTheUserOpacity_OutlineNeverDoes()
    {
        // The outline is the backdrop-independent half of the edge. Dimming it would
        // void the guarantee, so CircleOpacity applies to the core alone.
        var stack = Build(CursorRingStyle.WeightedSandwich, opacity: 0.4);

        Check.That(stack.Core.Opacity).IsEqualTo(0.4);
        Check.That(stack.Outer.Opacity).IsEqualTo(1.0);
    }

    [Fact]
    public void WeightedSandwich_IsSolid()
    {
        var stack = Build(CursorRingStyle.WeightedSandwich);

        Check.That(stack.Outer.DashPeriod).IsEqualTo(0.0);
        Check.That(stack.Core.DashPeriod).IsEqualTo(0.0);
    }

    [Fact]
    public void AchromaticReticle_IsALightOutlineOverADarkCore()
    {
        // Deliberately the INVERSE of the crosshair precedent in overlay-unification-spec
        // §4.1.3 (light mark, dark outline). Contrast-equivalent; this arrangement puts the
        // thicker carrying band on dark backdrops. Do not "correct" it — see spec §3.
        var stack = Build(CursorRingStyle.AchromaticReticle);

        Check.That(stack.Outer.Hex).IsEqualTo(Light);
        Check.That(stack.Outer.Thickness).IsEqualTo(7.0);
        Check.That(stack.Core.Hex).IsEqualTo(Halo);
        Check.That(stack.Core.Thickness).IsEqualTo(3.0);
        Check.That(stack.Outer.DashPeriod).IsEqualTo(0.0);
    }

    [Fact]
    public void AchromaticReticle_IgnoresTheMarkColour()
    {
        // The style has no hue, so the colour picker is swapped out for it in the UI.
        var stack = CursorRingStacks.For(
            CursorRingStyle.AchromaticReticle, 250, 3, 1.0, "#FF00FF", Halo, Light);

        Check.That(stack.Outer.Hex).IsEqualTo(Light);
        Check.That(stack.Core.Hex).IsEqualTo(Halo);
    }

    [Fact]
    public void BiTonalSegments_BothBandsShareOneThickness()
    {
        // Not a stack: the two strokes interleave along the arc at the same radius and
        // width. Equal thickness is also why the StrokeDashArray scaling trap of §4.1.3
        // does not arise here — both patterns divide by the same number.
        var stack = Build(CursorRingStyle.BiTonalSegments, thickness: 5);

        Check.That(stack.Outer.Hex).IsEqualTo(Halo);
        Check.That(stack.Outer.Thickness).IsEqualTo(5.0);
        Check.That(stack.Core.Hex).IsEqualTo(Mark);
        Check.That(stack.Core.Thickness).IsEqualTo(5.0);
    }

    [Fact]
    public void BiTonalSegments_CoreIsOffsetByExactlyOneDashLength()
    {
        // THE test for this style. A whole-cycle offset (2 * DashPeriod) would put the
        // bands in phase, the accent would cover the halo, and the style would silently
        // become a plain accent ring that still passes every contrast test.
        var stack = Build(CursorRingStyle.BiTonalSegments, thickness: 5);

        Check.That(stack.Outer.DashOffset).IsEqualTo(0.0);
        Check.That(stack.Core.DashOffset).IsEqualTo(stack.Core.DashPeriod);
        Check.That(stack.Outer.DashPeriod).IsEqualTo(stack.Core.DashPeriod);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(250)]
    [InlineData(333)]
    [InlineData(500)]
    public void BiTonalSegments_PatternClosesAtEveryLegalSize(double size)
    {
        // Asserted against the RETURNED period, not a restatement of the formula: written
        // as pure arithmetic this identity holds for any input and guards nothing. What it
        // guards here is rounding inside the implementation, which would leave a visible
        // seam where the dash pattern wraps.
        var stack = CursorRingStacks.For(
            CursorRingStyle.BiTonalSegments, size, 5, 1.0, Mark, Halo, Light);

        var cycles = Math.PI * size / (2 * stack.Core.DashPeriod);

        Check.That(Math.Abs(cycles - Math.Round(cycles))).IsStrictlyLessThan(1e-9);
        Check.That(Math.Round(cycles)).IsEqualTo(16.0);
    }

    [Theory]
    [InlineData(CursorRingStyle.WeightedSandwich)]
    [InlineData(CursorRingStyle.BiTonalSegments)]
    [InlineData(CursorRingStyle.AchromaticReticle)]
    public void EveryStyle_CoreCarriesTheUserOpacity_OutlineNeverDoes(CursorRingStyle style)
    {
        // Only WeightedSandwich pinned this, and only it passes a non-default opacity. At
        // 1.0 the property is untestable: a style whose arm swapped `opacity` and `1.0`
        // between its two bands would pass every other test in this file. The outline is
        // the backdrop-independent half of the edge, so dimming it voids the guarantee.
        var stack = CursorRingStacks.For(style, 250, 3, 0.4, Mark, Halo, Light);

        Check.WithCustomMessage($"{style} core").That(stack.Core.Opacity).IsEqualTo(0.4);
        Check.WithCustomMessage($"{style} outline").That(stack.Outer.Opacity).IsEqualTo(1.0);
    }

    [Theory]
    [InlineData(CursorRingStyle.WeightedSandwich)]
    [InlineData(CursorRingStyle.BiTonalSegments)]
    [InlineData(CursorRingStyle.AchromaticReticle)]
    public void EveryStyle_OuterIsNeverNarrowerThanTheCore(CursorRingStyle style)
    {
        // The property that makes a stack a stack: if the core were wider it would cover the
        // outline completely and there would be no internal edge left to see. Swept across
        // the whole legal thickness range, not just the presets.
        for (var thickness = 1.0; thickness <= 10.0; thickness += 0.5)
        {
            var stack = CursorRingStacks.For(style, 250, thickness, 1.0, Mark, Halo, Light);

            Check.WithCustomMessage($"{style} at thickness {thickness}")
                .That(stack.Outer.Thickness).IsGreaterOrEqualThan(stack.Core.Thickness);
        }
    }

    [Fact]
    public void Defaults_GiveWeightedSandwichItsIntendedWeights()
    {
        // A fresh install must get the style's real proportions, not its structure at the
        // weights that fail: a 2px core at 0.7 opacity is the shipped defect. Thickness is
        // 1.0, not 3.0: live testing found the ring too heavy, and the ring style preset no
        // longer writes thickness (only opacity), so this default is what a fresh install
        // actually sees — 1.0 is safe because the internal edge holds at any width.
        var settings = new CursorSettings();

        Check.That(settings.CircleThickness).IsEqualTo(1.0);
        Check.That(settings.CircleOpacity).IsEqualTo(1.0);
    }
}
