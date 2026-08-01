namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Drift guard for the XAML view of <see cref="Tokens"/>. Every Brush* key must
/// resolve and equal its Tokens counterpart. Catches a mistyped {x:Static} path
/// — which fails at runtime, not build time — and catches anyone pasting a raw
/// literal into the dictionary. Mirrors EyeCtrlSettingsEnumerationTests.
///
/// Shares a collection with <see cref="ThemeResourceApplierTests"/> so the two
/// pack-URI loaders never race to construct the one permitted WPF Application —
/// see <see cref="WpfPackSchemeCollection"/>.
/// </summary>
[Collection("WpfPackScheme")]
public class TokenDictionaryTests
{
    private static readonly Uri DictionaryUri = new(
        "pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml",
        UriKind.Absolute);

    /// <summary>
    /// The pack URI scheme is registered lazily by WPF, normally during
    /// Application startup. This project is a stand-alone xUnit v3 executable
    /// (no Application), so the very first pack:// Uri throws
    /// UriFormatException. Constructing an Application once is the documented
    /// way to register the scheme; guarded so only one is ever created.
    /// </summary>
    private static void EnsurePackScheme()
    {
        if (UriParser.IsKnownScheme("pack"))
        {
            return;
        }

        if (Application.Current is null)
        {
            _ = new Application();
        }
    }

    private static ResourceDictionary Load()
    {
        EnsurePackScheme();
        return new ResourceDictionary { Source = DictionaryUri };
    }

    [StaFact]
    public void EveryTokenBrush_IsExposedAndMatches()
    {
        var dict = Load();

        foreach (var field in typeof(Tokens).GetProperties(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.PropertyType == typeof(SolidColorBrush)))
        {
            var key = field.Name;   // e.g. BrushAccent
            Check.WithCustomMessage($"OverlayStyles.xaml is missing key '{key}'")
                .That(dict.Contains(key)).IsTrue();

            var fromXaml = (SolidColorBrush)dict[key]!;
            var fromCode = (SolidColorBrush)field.GetValue(null)!;
            Check.WithCustomMessage(
                    $"'{key}' is {fromXaml.Color} in OverlayStyles.xaml but "
                    + $"{fromCode.Color} in Tokens. The dictionary must derive from "
                    + "Tokens via {x:Static}, never restate a literal.")
                .That(fromXaml.Color).IsEqualTo(fromCode.Color);
        }
    }

    [StaFact]
    public void Dictionary_ExposesBorderThickness()
    {
        var dict = Load();
        Check.That(dict.Contains("BorderThicknessStandard")).IsTrue();
        Check.That(dict["BorderThicknessStandard"]).IsEqualTo(Tokens.BorderThicknessStandard);
    }

    [StaFact]
    public void Dictionary_ExposesTheThicknessFormDerivedFromTheToken()
    {
        // The XAML-consumable Thickness. This assertion is only possible because the value is
        // declared in the dictionary via UniformThicknessExtension; while it was assembled in
        // App.OnStartup this test could not see it, so the one theme-varying non-colour token
        // was the only one outside the drift guard.
        var dict = Load();
        var t = Tokens.BorderThicknessStandard;

        Check.WithCustomMessage(
                "OverlayStyles.xaml must expose StandardBorderThickness, or every "
                + "BorderThickness=\"{StaticResource StandardBorderThickness}\" throws at load.")
            .That(dict.Contains("StandardBorderThickness")).IsTrue();

        Check.WithCustomMessage(
                "StandardBorderThickness must be DERIVED from Tokens.BorderThicknessStandard, "
                + "never a restated literal — that is what lets a theme flip the double to 2 "
                + "and have every container border follow (§4.4).")
            .That(dict["StandardBorderThickness"]).IsEqualTo(new Thickness(t, t, t, t));
    }

    [StaFact]
    public void Dictionary_ContainsNoRawColourLiterals()
    {
        EnsurePackScheme();
        var xaml = new System.IO.StreamReader(
            Application.GetResourceStream(DictionaryUri)!.Stream).ReadToEnd();

        var literals = System.Text.RegularExpressions.Regex.Matches(xaml, @"#[0-9A-Fa-f]{6,8}\b")
            .Select(m => m.Value).Distinct().ToList();

        Check.WithCustomMessage(
                "OverlayStyles.xaml must contain no colour literals — every value "
                + "comes from Tokens via {x:Static}. Found: " + string.Join(", ", literals))
            .That(literals).IsEmpty();
    }
}
