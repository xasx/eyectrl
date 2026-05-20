namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core;
using EyeCtrl.Core.Settings;
using Xunit;

public class BackdropSettingsTests
{
    [Fact]
    public void Defaults_AreEnabledAcrylicWithOpacity()
    {
        var settings = new BackdropSettings();
        Assert.True(settings.Enabled);
        Assert.Equal(BackdropType.Acrylic, settings.Type);
        Assert.Equal(0.3, settings.Opacity);
    }

    [Fact]
    public void Validate_OpacityInRange_ReturnsNoErrors()
    {
        var settings = new BackdropSettings { Opacity = 0.5 };
        var errors = settings.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void SetOpacity_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var settings = new BackdropSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Opacity = 0.05);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Opacity = 0.95);
    }
}
