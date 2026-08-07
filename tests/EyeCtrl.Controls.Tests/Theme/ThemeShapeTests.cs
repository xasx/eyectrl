namespace EyeCtrl.Controls.Tests.Theme;

using System.Linq;
using System.Reflection;
using System.Windows;
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
    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

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

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void CornerRadii_AreOrderedAndNonNegative(ITheme theme)
    {
        // Mirrors the validator's gate-1 non-colour tail. A negative radius throws
        // when it reaches Border.CornerRadius (spec §4.1), so 0 is a real floor.
        Check.That(theme.CornerRadiusSmall).IsGreaterOrEqualThan(0.0);
        Check.That(theme.CornerRadiusStandard).IsGreaterOrEqualThan(theme.CornerRadiusSmall);
        Check.That(theme.CornerRadiusLarge).IsGreaterOrEqualThan(theme.CornerRadiusStandard);
        Check.WithCustomMessage(
                "CornerRadiusCircle is a fully-round sentinel and must outrank every real step.")
            .That(theme.CornerRadiusCircle).IsGreaterOrEqualThan(theme.CornerRadiusLarge);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void FontSizes_AreOrderedAndClearTheAccessibilityFloor(ITheme theme)
    {
        // 12 is the ramp floor spec §8.4 chose, and §9 gates it in the validator.
        // Keep this number identical to the validator's or the two guards disagree.
        Check.WithCustomMessage(
                $"{theme.Name} sets body text below the 12px floor, which is the guarantee "
                + "that makes the 9->12 raise permanent rather than one release's decision.")
            .That(theme.FontSizeBody).IsGreaterOrEqualThan(12.0);
        Check.That(theme.FontSizeBodyLarge).IsGreaterOrEqualThan(theme.FontSizeBody);
        Check.That(theme.FontSizeSubtitle).IsGreaterOrEqualThan(theme.FontSizeBodyLarge);
        Check.That(theme.FontSizeTitle).IsGreaterOrEqualThan(theme.FontSizeSubtitle);
        Check.That(theme.FontSizeDisplay).IsGreaterOrEqualThan(theme.FontSizeTitle);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void FontWeights_AreOrderedAndClearTheEmphasisStep(ITheme theme)
    {
        Check.WithCustomMessage(
                "FontWeightMedium is the 600 emphasis step, not FontWeights.Medium (500).")
            .That(theme.FontWeightMedium.ToOpenTypeWeight()).IsGreaterOrEqualThan(600);
        Check.That(theme.FontWeightStrong.ToOpenTypeWeight())
            .IsStrictlyGreaterThan(theme.FontWeightMedium.ToOpenTypeWeight());
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void FontFamily_IsDeclaredAndResolves(ITheme theme)
    {
        Check.That(theme.FontFamilyStandard).IsNotEmpty();
        Check.WithCustomMessage(
                "FontStandard must derive from FontFamilyStandard, never restate it.")
            .That(theme.FontStandard.Source).IsEqualTo(theme.FontFamilyStandard);
    }

    [Fact]
    public void FontStandard_IsCachedNotRebuiltPerAccess()
    {
        // Same reasoning as BrushesAreCachedNotRebuiltPerAccess: overlays rebuild at gaze rate.
        Check.That(MidnightTheme.Instance.FontStandard)
             .IsSameReferenceAs(MidnightTheme.Instance.FontStandard);
    }

    [Fact]
    public void AllThreeThemes_DeclareIdenticalGeometryAndType()
    {
        // Spec §5 decision 6: the mechanism is theme-varying, the values are not — yet.
        // If a theme deliberately diverges later, delete this test and say why in the commit.
        //
        // This is not made redundant by ThemeParityTests: parity proves C# and the
        // validator AGREE, and the validator's gates check only ordering plus the 12px
        // floor. This is the only guard on what they agree ON, and on the three themes
        // agreeing with each other.
        foreach (var theme in ThemeRegistry.All)
        {
            Check.That(theme.CornerRadiusSmall).IsEqualTo(4.0);
            Check.That(theme.CornerRadiusStandard).IsEqualTo(8.0);
            Check.That(theme.CornerRadiusLarge).IsEqualTo(12.0);
            Check.That(theme.CornerRadiusCircle).IsEqualTo(9999.0);
            Check.That(theme.FontSizeBody).IsEqualTo(12.0);
            Check.That(theme.FontSizeBodyLarge).IsEqualTo(14.0);
            Check.That(theme.FontSizeSubtitle).IsEqualTo(18.0);
            Check.That(theme.FontSizeTitle).IsEqualTo(24.0);
            Check.That(theme.FontSizeDisplay).IsEqualTo(34.0);
            Check.That(theme.FontFamilyStandard).IsEqualTo("Segoe UI Variable Text, Segoe UI");
            Check.That(theme.FontWeightMedium).IsEqualTo(FontWeights.SemiBold);
            Check.That(theme.FontWeightStrong).IsEqualTo(FontWeights.Bold);
        }
    }
}
