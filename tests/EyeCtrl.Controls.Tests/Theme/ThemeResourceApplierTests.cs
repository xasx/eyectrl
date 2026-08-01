namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The applier is what makes a theme visible to the 106 {StaticResource Brush*}
/// lookups. It writes explicitly rather than relying on OverlayStyles.xaml being
/// parsed after the theme is chosen — parse timing is not something startup can
/// control, and getting it wrong would silently ship Midnight.
///
/// Shares a collection with <see cref="TokenDictionaryTests"/> so the two
/// pack-URI loaders never race to construct the one permitted WPF Application —
/// see <see cref="WpfPackSchemeCollection"/>.
/// </summary>
[Collection("WpfPackScheme")]
public class ThemeResourceApplierTests
{
    private static readonly Uri DictionaryUri = new(
        "pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml",
        UriKind.Absolute);

    private static void EnsurePackScheme()
    {
        if (UriParser.IsKnownScheme("pack")) return;
        if (Application.Current is null) _ = new Application();
    }

    [Fact]
    public void Apply_WritesEveryBrushOfTheGivenTheme()
    {
        var target = new ResourceDictionary();
        ThemeResourceApplier.Apply(target, ContrastTheme.Instance);

        Check.That(((SolidColorBrush)target["BrushAccent"]!).Color)
             .IsEqualTo(ContrastTheme.Instance.Accent);
        Check.That(((SolidColorBrush)target["BrushSurfaceDark"]!).Color)
             .IsEqualTo(ContrastTheme.Instance.SurfaceDark);
        Check.That(((SolidColorBrush)target["BrushAccentDim10"]!).Opacity).IsEqualTo(0.10);
    }

    [Fact]
    public void Apply_WritesBothFormsOfTheThicknessToken()
    {
        var target = new ResourceDictionary();
        ThemeResourceApplier.Apply(target, ContrastTheme.Instance);

        // Contrast is the theme that actually exercises this: 2, not 1 (§4.4).
        Check.That(target["BorderThicknessStandard"]).IsEqualTo(2.0);
        Check.That(target["StandardBorderThickness"]).IsEqualTo(new Thickness(2, 2, 2, 2));
    }

    [Fact]
    public void Apply_IsIdempotentAndSwitchable()
    {
        var target = new ResourceDictionary();
        ThemeResourceApplier.Apply(target, ContrastTheme.Instance);
        ThemeResourceApplier.Apply(target, EmberTheme.Instance);

        Check.That(((SolidColorBrush)target["BrushAccent"]!).Color)
             .IsEqualTo(EmberTheme.Instance.Accent);
        Check.That(target["BorderThicknessStandard"]).IsEqualTo(1.0);
    }

    [StaFact]
    public void Apply_CoversEveryKeyTheDictionaryDeclares()
    {
        // If OverlayStyles.xaml grows a key the applier does not write, that key
        // keeps its parse-time value and silently stays on whatever theme was
        // active when WPF parsed the dictionary.
        EnsurePackScheme();
        var declared = new ResourceDictionary { Source = DictionaryUri }
            .Keys.Cast<object>().Select(k => k.ToString()!).ToList();

        var applied = new ResourceDictionary();
        ThemeResourceApplier.Apply(applied, MidnightTheme.Instance);
        var written = applied.Keys.Cast<object>().Select(k => k.ToString()!).ToList();

        var missing = declared.Except(written).ToList();
        Check.WithCustomMessage(
                "OverlayStyles.xaml declares keys the applier never writes, so a theme "
                + "switch would not reach them: " + string.Join(", ", missing))
            .That(missing).IsEmpty();
    }
}
