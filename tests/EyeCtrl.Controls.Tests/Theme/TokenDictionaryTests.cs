namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.IO;
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
///
/// **The completeness these two tests demand is why unreferenced keys exist, and they are not
/// dead weight.** Measured on this branch: 16 of OverlayStyles.xaml's 41 keys have no
/// <c>{StaticResource}</c> or <c>{DynamicResource}</c> consumer anywhere in src, and nothing in
/// src looks a resource up by string either (no <c>Resources[...]</c>, <c>FindResource</c> or
/// <c>TryFindResource</c> call exists), so they are genuinely unreachable *as resources* today.
/// Ten are keyboard, toggle and marker brushes that their surfaces currently set from C# via
/// <c>Tokens.</c>; the rest are the raw doubles and the family string, whose XAML-consumable
/// forms are separate keys.
///
/// A reviewer proposed deleting them. They must not be: <see cref="EveryTokenBrush_IsExposedAndMatches"/>
/// and <see cref="EveryNonBrushToken_IsExposedAndMatches"/> require a key for EVERY
/// <see cref="Tokens"/> member, so deleting any of them turns this file red. That is deliberate —
/// the dictionary is a complete XAML view of the token surface, not a list of what happens to be
/// referenced this week, so an overlay author can reach for any token without first having to
/// declare it. Recorded here so the observation is not rediscovered and acted on.
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

    /// <summary>
    /// Reads the raw XAML source file from disk rather than <see cref="Application.GetResourceStream"/>,
    /// because WPF compiles <c>&lt;Page&gt;</c> dictionaries into binary BAML in the assembly resource stream.
    /// </summary>
    private static string ReadOverlayStylesXamlSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.WithCustomMessage("Could not locate EyeCtrl.slnx above AppContext.BaseDirectory")
            .That(dir).IsNotNull();

        var path = Path.Combine(dir!.FullName, "src", "EyeCtrl.Controls", "Theme", "OverlayStyles.xaml");
        var xaml = File.ReadAllText(path);
        Check.That(xaml).Contains("<ResourceDictionary");
        return xaml;
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
    public void EveryNonBrushToken_IsExposedAndMatches()
    {
        var dict = Load();

        foreach (var property in NonBrushTokenProperties())
        {
            var key = property.Name;
            Check.WithCustomMessage($"OverlayStyles.xaml is missing key '{key}'")
                .That(dict.Contains(key)).IsTrue();
            Check.WithCustomMessage(
                    $"'{key}' must be DERIVED from Tokens via {{x:Static}}, never restated.")
                .That(dict[key]).IsEqualTo(property.GetValue(null));
        }
    }

    /// <summary>
    /// The non-brush tokens' value equality runs under the Midnight ambient, where a restated
    /// literal is indistinguishable from the live value (12 for FontSizeBody, 1 for
    /// BorderThicknessStandard, "SemiBold" for FontWeightMedium). The brushes have
    /// <see cref="Dictionary_ContainsNoRawColourLiterals"/> for exactly this drift; the
    /// non-brush keys get the same treatment here at the SOURCE level: every key's declaring
    /// element must reference the Tokens member it claims to expose, so a pasted literal —
    /// which silently stops following a theme — fails even while its value still matches.
    /// </summary>
    [StaFact]
    public void EveryNonBrushToken_IsDeclaredAsATokensReference()
    {
        var xaml = ReadOverlayStylesXamlSource();

        foreach (var property in NonBrushTokenProperties())
        {
            var keyIndex = xaml.IndexOf($"x:Key=\"{property.Name}\"", StringComparison.Ordinal);
            Check.WithCustomMessage($"OverlayStyles.xaml is missing key '{property.Name}'")
                .That(keyIndex).IsStrictlyGreaterThan(-1);

            // The whole declaring element: back to its opening '<', forward past its
            // self-close "/>" or through a closing "</...>" (a restated literal's value sits
            // in the element body, so the body must be inside the scanned range).
            var elementStart = xaml.LastIndexOf('<', keyIndex);
            var selfClose = xaml.IndexOf("/>", keyIndex, StringComparison.Ordinal);
            var closing = xaml.IndexOf("</", keyIndex, StringComparison.Ordinal);
            var end = closing < 0 || (selfClose >= 0 && selfClose < closing)
                ? selfClose + 2
                : xaml.IndexOf('>', closing) + 1;
            var declaration = xaml[elementStart..end];

            Check.WithCustomMessage(
                    $"'{property.Name}' must DERIVE from Tokens.{property.Name} via "
                    + "{x:Static} (or a Member= path) — a restated literal passes the value "
                    + "comparison under the Midnight ambient but silently stops following a theme.")
                .That(declaration.Contains($"theme:Tokens.{property.Name}", StringComparison.Ordinal))
                .IsTrue();
        }
    }

    private static System.Collections.Generic.IEnumerable<System.Reflection.PropertyInfo> NonBrushTokenProperties() =>
        typeof(Tokens).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType != typeof(SolidColorBrush)
                     && p.PropertyType != typeof(Color)
                     && p.Name != nameof(Tokens.Current));

    [StaFact]
    public void Dictionary_ExposesTheFourCornerRadiusForms()
    {
        // WPF cannot convert a double resource to a CornerRadius, so the CornerRadius
        // form must be its own key. Verified resolvable through {StaticResource} in the
        // real merged-dictionary topology (spec §6.5) — no markup extension needed.
        //
        // It is also the SOLE guard that each Tokens.<X>CornerRadius derives from its own
        // CornerRadius<X> primitive rather than some other one: EveryNonBrushToken_IsExposedAndMatches
        // compares dict[key] against Tokens.<key>, so both sides move together and a swapped
        // derivation would not fail it. Mutate the derivation and this is the only one of 83
        // Controls tests that fails.
        var dict = Load();
        foreach (var (key, expected) in new (string, CornerRadius)[]
                 {
                     ("SmallCornerRadius", new CornerRadius(Tokens.CornerRadiusSmall)),
                     ("StandardCornerRadius", new CornerRadius(Tokens.CornerRadiusStandard)),
                     ("LargeCornerRadius", new CornerRadius(Tokens.CornerRadiusLarge)),
                     ("CircleCornerRadius", new CornerRadius(Tokens.CornerRadiusCircle)),
                 })
        {
            Check.WithCustomMessage(
                    $"OverlayStyles.xaml must expose {key}, or every "
                    + $"CornerRadius=\"{{StaticResource {key}}}\" throws at load.")
                .That(dict.Contains(key)).IsTrue();
            Check.That(dict[key]).IsEqualTo(expected);
        }
    }

    [StaFact]
    public void Dictionary_ExposesBothFontKeysWithTheRightTypes()
    {
        // FontFamilyStandard is the raw string; FontStandard is the FontFamily object.
        // Using the wrong one throws XamlParseException at load — no type converter runs
        // on a StaticResource assignment (spec §6.4). Documents that two-key hazard
        // explicitly; it is not the only thing catching it — EveryNonBrushToken_IsExposedAndMatches
        // would already catch a swapped Member= path, and the .Source assertion duplicates
        // ThemeShapeTests.FontFamily_IsDeclaredAndResolves.
        var dict = Load();
        Check.WithCustomMessage("OverlayStyles.xaml is missing key 'FontFamilyStandard'")
            .That(dict.Contains("FontFamilyStandard")).IsTrue();
        Check.That(dict["FontFamilyStandard"]).IsInstanceOf<string>();

        Check.WithCustomMessage("OverlayStyles.xaml is missing key 'FontStandard'")
            .That(dict.Contains("FontStandard")).IsTrue();
        Check.That(dict["FontStandard"]).IsInstanceOf<FontFamily>();
        Check.That(((FontFamily)dict["FontStandard"]!).Source).IsEqualTo(Tokens.FontFamilyStandard);
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
        var xaml = ReadOverlayStylesXamlSource();

        var literals = System.Text.RegularExpressions.Regex.Matches(xaml, @"#[0-9A-Fa-f]{6,8}\b")
            .Select(m => m.Value).Distinct().ToList();

        Check.WithCustomMessage(
                "OverlayStyles.xaml must contain no colour literals — every value "
                + "comes from Tokens via {x:Static}. Found: " + string.Join(", ", literals))
            .That(literals).IsEmpty();
    }
}
