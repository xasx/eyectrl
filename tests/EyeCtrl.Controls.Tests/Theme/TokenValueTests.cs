namespace EyeCtrl.Controls.Tests.Theme;

using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Pins every palette token to its docs/design/overlay-unification-spec.md §4.1
/// value. These are validated by tools/palette_validate.py — changing one here
/// without re-running that script invalidates §9's measured tables.
/// </summary>
public class TokenValueTests
{
    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    [Theory]
    // Surfaces
    [InlineData(nameof(Tokens.SurfaceDark), "#FA1A1A2E")]
    [InlineData(nameof(Tokens.SurfaceMedium), "#FA2A2A44")]
    [InlineData(nameof(Tokens.SurfaceLight), "#FF3A3A5E")]
    [InlineData(nameof(Tokens.SurfaceKey), "#FF2A2A3E")]
    [InlineData(nameof(Tokens.SurfaceKeyModifier), "#FF3E3E64")]
    [InlineData(nameof(Tokens.SurfaceKeySwitch), "#FF38527F")]
    [InlineData(nameof(Tokens.SurfacePrediction), "#FF2F5340")]
    // Text and borders
    [InlineData(nameof(Tokens.BorderSubtle), "#FF616BA0")]
    [InlineData(nameof(Tokens.BorderFocus), "#FFA5F3FC")]
    [InlineData(nameof(Tokens.TextPrimary), "#FFD6DEF5")]
    [InlineData(nameof(Tokens.TextSecondary), "#FF9AA4C8")]
    [InlineData(nameof(Tokens.TextDim), "#FF868FB5")]
    // Accent and semantics
    [InlineData(nameof(Tokens.Accent), "#FFA5F3FC")]
    [InlineData(nameof(Tokens.StrokeHalo), "#FF0A0A14")]
    [InlineData(nameof(Tokens.AccentSuccess), "#FF7EE787")]
    [InlineData(nameof(Tokens.AccentWarning), "#FFE8A33C")]
    [InlineData(nameof(Tokens.AccentDanger), "#FFFF5C7A")]
    [InlineData(nameof(Tokens.ToggleOn), "#FF4A8F5C")]
    [InlineData(nameof(Tokens.ToggleOff), "#FF2E2E42")]
    [InlineData(nameof(Tokens.ModalBackdrop), "#B3000000")]
    public void Token_HasSpecifiedValue(string name, string expected)
    {
        var field = typeof(Tokens).GetField(name);
        Check.WithCustomMessage($"Tokens.{name} does not exist").That(field).IsNotNull();

        var actual = Hex((Color)field!.GetValue(null)!);
        Check.WithCustomMessage(
                $"Tokens.{name} is {actual}, spec §4.1 says {expected}. "
                + "If this change is intended, update §4.1 AND re-run "
                + "tools/palette_validate.py before changing this test.")
            .That(actual).IsEqualTo(expected);
    }

    [Fact]
    public void EveryColourToken_HasAMatchingFrozenBrush()
    {
        foreach (var field in typeof(Tokens).GetFields()
                     .Where(f => f.FieldType == typeof(Color)))
        {
            var brushField = typeof(Tokens).GetField("Brush" + field.Name);
            Check.WithCustomMessage($"Tokens.Brush{field.Name} is missing")
                .That(brushField).IsNotNull();

            var brush = (SolidColorBrush)brushField!.GetValue(null)!;
            Check.That(brush.Color).IsEqualTo((Color)field.GetValue(null)!);
            Check.WithCustomMessage($"Tokens.Brush{field.Name} must be frozen")
                .That(brush.IsFrozen).IsTrue();
        }
    }

    [Fact]
    public void BorderThicknessStandard_IsOnePixel()
    {
        // Theme-varying per spec §4.4 — Contrast uses 2. Midnight is 1.
        Check.That(Tokens.BorderThicknessStandard).IsEqualTo(1.0);
    }
}
