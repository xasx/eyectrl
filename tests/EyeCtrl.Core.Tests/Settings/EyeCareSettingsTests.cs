namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class EyeCareSettingsTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var s = new EyeCareSettings();

        Check.That(s.Enabled).IsFalse();
        Check.That(s.IntervalMinutes).IsEqualTo(20);
        Check.That(s.NaturalBreakSeconds).IsEqualTo(30);
        Check.That(s.ExerciseDurationSeconds).IsEqualTo(45);
        Check.That(s.BannerAutoSnoozeSeconds).IsEqualTo(15);
        Check.That(s.SnoozeMinutes).IsEqualTo(5);
        Check.That(s.FatigueMonitorEnabled).IsFalse();
        Check.That(s.FatigueThreshold).IsEqualTo(0.75);
    }

    [Fact]
    public void Validate_OutOfRange_ReturnsErrors()
    {
        var s = new EyeCareSettings { IntervalMinutes = 0 };

        var errors = s.Validate();

        Check.That(errors).HasElementThatMatches(e => e.Contains(nameof(EyeCareSettings.IntervalMinutes)));
    }

    [Fact]
    public void Validate_InRange_ReturnsNoErrors()
    {
        var s = new EyeCareSettings();

        Check.That(s.Validate()).IsEmpty();
    }
}
