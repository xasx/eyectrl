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
        Check.That(settings.Enabled).IsTrue();
        Check.That(settings.Type).IsEqualTo(BackdropType.Acrylic);
        Check.That(settings.Opacity).IsEqualTo(0.3);
    }

    [Fact]
    public void Validate_OpacityInRange_ReturnsNoErrors()
    {
        var settings = new BackdropSettings { Opacity = 0.5 };
        var errors = settings.Validate();
        Check.That(errors).IsEmpty();
    }

    [Fact]
    public void SetOpacity_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var settings = new BackdropSettings();
        Check.ThatCode(() => settings.Opacity = 0.05).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => settings.Opacity = 0.95).Throws<ArgumentOutOfRangeException>();
    }
}
