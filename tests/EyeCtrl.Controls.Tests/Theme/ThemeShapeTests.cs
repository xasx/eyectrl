namespace EyeCtrl.Controls.Tests.Theme;

using System.Linq;
using System.Reflection;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Structural guarantees every theme must satisfy, independent of its values.
///
/// The brush/colour pairing test is load-bearing: ThemeBase derives brushes from
/// abstract colour properties, so a theme whose override read an instance field
/// instead of returning a constant would produce a brush built from default(Color).
/// This test makes that failure loud instead of silent.
/// </summary>
public class ThemeShapeTests
{
    public static TheoryData<ITheme> AllThemes() => new()
    {
        MidnightTheme.Instance,
        EmberTheme.Instance,
        ContrastTheme.Instance,
    };

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryColour_HasAMatchingFrozenBrush(ITheme theme)
    {
        var colours = typeof(ITheme).GetProperties()
            .Where(p => p.PropertyType == typeof(Color))
            .ToList();

        Check.WithCustomMessage("ITheme declares no Color members — wrong type reflected")
            .That(colours).Not.IsEmpty();

        foreach (var colour in colours)
        {
            var brushProperty = typeof(ITheme).GetProperty("Brush" + colour.Name);
            Check.WithCustomMessage($"ITheme.Brush{colour.Name} is missing")
                .That(brushProperty).IsNotNull();

            var brush = (SolidColorBrush)brushProperty!.GetValue(theme)!;
            Check.WithCustomMessage(
                    $"{theme.Name}.Brush{colour.Name} is {brush.Color} but "
                    + $"{theme.Name}.{colour.Name} is {colour.GetValue(theme)}. "
                    + "ThemeBase derives brushes from the colour properties; an "
                    + "override that reads a field instead of returning a constant "
                    + "breaks that.")
                .That(brush.Color).IsEqualTo((Color)colour.GetValue(theme)!);

            Check.WithCustomMessage($"{theme.Name}.Brush{colour.Name} must be frozen")
                .That(brush.IsFrozen).IsTrue();
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void DerivedAccentDim10_IsAccentAtTenPercent(ITheme theme)
    {
        Check.That(theme.BrushAccentDim10.Color).IsEqualTo(theme.Accent);
        Check.That(theme.BrushAccentDim10.Opacity).IsEqualTo(0.10);
        Check.That(theme.BrushAccentDim10.IsFrozen).IsTrue();
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void BorderThickness_IsAtLeastOnePixel(ITheme theme)
    {
        // Mirrors the validator's non-colour gate (tools/palette_validate.py:319-322).
        Check.That(theme.BorderThicknessStandard).IsGreaterOrEqualThan(1.0);
    }

    [Fact]
    public void Midnight_UsesTheOnePixelBorder()
    {
        Check.That(MidnightTheme.Instance.BorderThicknessStandard).IsEqualTo(1.0);
    }

    [Fact]
    public void BrushesAreCachedNotRebuiltPerAccess()
    {
        // 106 XAML lookups and 131 C# call sites read these on redraw at gaze rate.
        // A new SolidColorBrush per access would allocate in the render path.
        Check.That(MidnightTheme.Instance.BrushAccent)
             .IsSameReferenceAs(MidnightTheme.Instance.BrushAccent);
    }
}
