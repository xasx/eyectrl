namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// F17's typed-prefix brush: the already-typed part of a completion is drawn in the theme's
/// de-emphasised grey, but it must still be readable on the prediction surface. TextSecondary
/// measures only 3.50:1 on Midnight's <see cref="ITheme.SurfacePrediction"/>, so the derived
/// brush starts there and lightens toward TextPrimary until it clears the 4.5:1 body-text floor.
/// The other three themes keep TextSecondary unchanged — that half is pinned here so the
/// derived brush cannot silently become "just another light text colour".
/// </summary>
public class PredictionPrefixContrastTests
{
    private const double BodyTextFloor = 4.5;

    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TypedPrefix_ClearsTheBodyTextFloor_OnThePredictionSurface(ITheme theme)
    {
        var ratio = Contrast(theme.BrushPredictionPrefix.Color, theme.SurfacePrediction);

        Check.WithCustomMessage(
                $"{theme.Name}: the typed prefix is {ratio:F2}:1 on SurfacePrediction, below the "
                + $"{BodyTextFloor}:1 body-text floor. The user reads this text while gazing at it.")
            .That(ratio).IsGreaterOrEqualThan(BodyTextFloor);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TypedPrefix_IsTheSecondaryWhereTheSecondaryAlreadyClears_TheFloor(ITheme theme)
    {
        var secondary = Contrast(theme.TextSecondary, theme.SurfacePrediction);
        if (secondary < BodyTextFloor) return;   // Midnight: the derived lightening is the point

        Check.WithCustomMessage(
                $"{theme.Name}: TextSecondary already clears the floor ({secondary:F2}:1), so the "
                + "typed prefix must be that de-emphasised grey rather than a brighter variant.")
            .That(theme.BrushPredictionPrefix.Color).IsEqualTo(theme.TextSecondary);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TypedPrefix_StaysADeEmphasisedGrey_NotTheBodyColour(ITheme theme)
    {
        var typed = theme.BrushPredictionPrefix.Color;
        var primary = theme.TextPrimary;

        Check.WithCustomMessage(
                $"{theme.Name}: the typed prefix is exactly TextPrimary, so the already-typed part "
                + "cannot be told apart from the rest of an ExactPrefix suggestion.")
            .That((typed.R, typed.G, typed.B)).Not.IsEqualTo((primary.R, primary.G, primary.B));
        Check.That(theme.BrushPredictionPrefix.IsFrozen).IsTrue();
    }

    [Fact]
    public void TypedPrefix_IsCachedPerTheme()
    {
        Check.That(MidnightTheme.Instance.BrushPredictionPrefix)
            .IsSameReferenceAs(MidnightTheme.Instance.BrushPredictionPrefix);
    }

    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var lighter = Math.Max(la, lb);
        var darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color colour) =>
        0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);

    private static double Channel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
