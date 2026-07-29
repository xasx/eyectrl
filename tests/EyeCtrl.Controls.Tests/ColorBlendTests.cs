namespace EyeCtrl.Controls.Tests;

using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Invariants for <see cref="ColorBlend"/>: the linear interpolation and dwell-highlight
/// helpers used across buttons, toggles, tabs and the keyboard. Endpoint and identity
/// behaviour, plus the Accent-targeting contract of <c>HighlightColor</c>.
/// </summary>
public class ColorBlendTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);

    [Fact]
    public void Blend_AtZero_ReturnsFrom()
    {
        Check.That(ColorBlend.Blend(Red, Blue, 0)).IsEqualTo(Red);
    }

    [Fact]
    public void Blend_AtOne_ReturnsTo()
    {
        Check.That(ColorBlend.Blend(Red, Blue, 1)).IsEqualTo(Blue);
    }

    [Fact]
    public void Blend_SameColour_IsIdentity()
    {
        // Blending a colour toward itself at any t yields that colour.
        Check.That(ColorBlend.Blend(Red, Red, 0.5)).IsEqualTo(Red);
        Check.That(ColorBlend.Blend(Red, Red, 0.25)).IsEqualTo(Red);
    }

    [Fact]
    public void Blend_AtHalf_IsTheMidpoint()
    {
        // (byte)(255 * 0.5) truncates to 127, not 128 — the blend uses byte-cast truncation.
        var mid = ColorBlend.Blend(Red, Blue, 0.5);
        Check.That(mid).IsEqualTo(Color.FromRgb(127, 0, 127));
    }

    [Fact]
    public void Blend_ClampsOutOfRangeParameters()
    {
        Check.That(ColorBlend.Blend(Red, Blue, -0.5)).IsEqualTo(Red);   // clamps to 0
        Check.That(ColorBlend.Blend(Red, Blue, 1.5)).IsEqualTo(Blue);   // clamps to 1
    }

    [Fact]
    public void HighlightColor_AtZeroProgress_ReturnsBaseColour()
    {
        // No dwell progress -> the resting colour (Accent is the blend target, not applied).
        Check.That(ColorBlend.HighlightColor(Red, 0)).IsEqualTo(Red);
    }

    [Fact]
    public void HighlightColor_AtFullProgressAndFactor_IsAccent()
    {
        // progress=1, factor=1 -> fully blended toward Accent.
        Check.That(ColorBlend.HighlightColor(Red, 1, 1)).IsEqualTo(Tokens.Accent);
    }
}
