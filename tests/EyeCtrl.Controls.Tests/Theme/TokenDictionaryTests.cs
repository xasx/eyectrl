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
/// </summary>
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

        foreach (var field in typeof(Tokens).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.FieldType == typeof(SolidColorBrush)))
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
