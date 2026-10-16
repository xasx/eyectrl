namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core;
using EyeCtrl.Core.Settings;
using Xunit;

public class BackdropSettingsTests
{
    [Fact]
    public void Defaults_AreEnabledGlassWithOpacity()
    {
        var settings = new BackdropSettings();
        Check.That(settings.Enabled).IsTrue();
        Check.That(settings.Type).IsEqualTo(BackdropType.Glass);
        Check.That(settings.Opacity).IsEqualTo(0.3);
    }

    [Fact]
    public void TheEnum_OffersExactlyGlassAndSolidDarken()
    {
        // The Settings combo derives its codes from the enum (AdvancedPageSource), so the member
        // set IS the option list: the legacy blur names must not reappear as members.
        Check.That(Enum.GetNames<BackdropType>()).ContainsExactly("Glass", "SolidDarken");
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
