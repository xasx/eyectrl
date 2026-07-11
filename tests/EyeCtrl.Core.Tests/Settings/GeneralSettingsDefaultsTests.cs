namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;

public class GeneralSettingsDefaultsTests
{
    [Fact]
    public void Defaults_StartOnBootFalse_StartPausedFalse_GazeProviderAuto()
    {
        var g = new GeneralSettings();
        Check.That(g.StartOnBoot).IsFalse();
        Check.That(g.StartPaused).IsFalse();
        Check.That(g.GazeProviderType).IsEqualTo(GazeProviderType.Auto);
    }

    [Fact]
    public void Validate_DefaultInstance_HasNoErrors()
    {
        Check.That(new GeneralSettings().Validate()).IsEmpty();
    }
}
