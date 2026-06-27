namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class ScrollSettingsTests
{
    [Fact]
    public void DefaultSensitivityIsOne()
    {
        var s = new ScrollSettings();
        Check.That(s.Sensitivity).IsCloseTo(1.0, 1e-6);
    }

    [Fact]
    public void SensitivityClampedToRange()
    {
        var s = new ScrollSettings();
        Check.ThatCode(() => s.Sensitivity = 3.0).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => s.Sensitivity = 0.1).Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ValidSensitivityRangeDoesNotThrow()
    {
        var s = new ScrollSettings();
        s.Sensitivity = 0.5;
        Check.That(s.Sensitivity).IsCloseTo(0.5, 1e-6);
        s.Sensitivity = 2.0;
        Check.That(s.Sensitivity).IsCloseTo(2.0, 1e-6);
    }

    [Fact]
    public void ValidatePassesForDefaults()
    {
        var s = new ScrollSettings();
        Check.That(s.Validate()).IsEmpty();
    }
}
