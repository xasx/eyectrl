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

    [Theory]
    [InlineData(0.0)]     // the widened floor: "no darkening" — valid for both types
    [InlineData(0.95)]    // the widened ceiling: "almost black"
    public void Validate_AtTheWidenedEnds_ReturnsNoErrors(double opacity)
    {
        // The guard and Validate share OpacityMin/OpacityMax; a value the guard accepts must also
        // validate, or Save would silently drop it (the widened range is the feature).
        var settings = new BackdropSettings { Opacity = opacity };
        Check.That(settings.Opacity).IsEqualTo(opacity);
        Check.That(settings.Validate()).IsEmpty();
    }

    [Fact]
    public void SetOpacity_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        // Pin the widened bounds as LITERALS: the guard, Validate and the slider all derive from
        // the constants, so their VALUES are what these two rows hold still (0.0 and 0.95 are the
        // owner's range; −0.01 and 0.96 must stay rejected).
        var settings = new BackdropSettings();
        Check.ThatCode(() => settings.Opacity = -0.01).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => settings.Opacity = 0.96).Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TheRangeConstants_AreTheOwnersRange()
    {
        Check.That(BackdropSettings.OpacityMin).IsEqualTo(0.0);
        Check.That(BackdropSettings.OpacityMax).IsEqualTo(0.95);
    }
}
