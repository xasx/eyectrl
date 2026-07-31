namespace EyeCtrl.App.Tests;

using System.Linq;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// EyeCtrl.Core cannot reference EyeCtrl.Controls, so ThemeSettings restates the
/// valid theme names. This is the only project referencing both, and the only
/// thing keeping the two lists in step — a name in one but not the other is
/// either an unpickable theme or a setting that fails validation.
/// </summary>
public class ThemeSettingsNamesTests
{
    [Fact]
    public void ValidNames_MatchTheRegistry() =>
        Check.That(ThemeSettings.ValidNames).ContainsExactly(ThemeRegistry.Names);

    [Fact]
    public void DefaultThemeName_ResolvesToTheShippingPalette() =>
        Check.That(ThemeRegistry.Resolve(new ThemeSettings().Name).Name).IsEqualTo("Midnight");

    [Fact]
    public void EveryValidName_Resolves()
    {
        foreach (var name in ThemeSettings.ValidNames)
            Check.WithCustomMessage($"ThemeSettings offers '{name}' but the registry cannot resolve it")
                .That(ThemeRegistry.Resolve(name).Name).IsEqualTo(name);
    }
}
