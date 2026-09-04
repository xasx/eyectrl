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
}
