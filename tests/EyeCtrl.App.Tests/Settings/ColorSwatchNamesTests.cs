namespace EyeCtrl.App.Tests.Settings;

using System.Collections.Generic;
using EyeCtrl.App.Views;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// T02 fixup-01, smaller item: the Settings host's swatch names and the library's preset list are
/// otherwise coupled only by a run-time throw in <see cref="DwellColorPicker"/>'s constructor.
/// The host hard-codes its names while the library derives its list from the palette, so a fifth
/// preset (or a theme with a different count) would break the picker only when a user opens it.
/// This pins the counts headlessly, for every palette the host could be running under.
/// </summary>
public class ColorSwatchNamesTests
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
    public void TheHostNames_CountMatchesTheSwatchesTheLibraryOffers(ITheme theme)
    {
        Check.WithCustomMessage(
                $"the host offers {SettingsWindow.ColorSwatchNames.Length} swatch names but "
                + $"PresetsFor({theme.Name}) has {DwellColorPicker.PresetsFor(theme).Length} entries; "
                + "the picker throws when the list opens. Add the name (a key, from T07 on) here or "
                + "stop offering the preset.")
            .That(SettingsWindow.ColorSwatchNames.Length)
            .IsEqualTo(DwellColorPicker.PresetsFor(theme).Length);
    }
}
