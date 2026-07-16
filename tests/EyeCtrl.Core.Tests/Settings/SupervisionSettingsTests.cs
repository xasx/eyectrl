namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class SupervisionSettingsTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var s = new SupervisionSettings();
        Check.That(s.Enabled).IsFalse();
        Check.That(s.MaxRetries).IsEqualTo(3);
        Check.That(s.CrashWindowSeconds).IsEqualTo(120);
        Check.That(s.HangThresholdSeconds).IsEqualTo(20);
        Check.That(s.StartupGraceSeconds).IsEqualTo(60);
        Check.That(s.FallbackAtOrder).ContainsExactly("TDControl", "SnapCoreFirst", "WindowsEyeControl");
    }

    [Fact]
    public void Validate_OutOfRange_ReturnsErrors()
    {
        var s = new SupervisionSettings { MaxRetries = 0, CrashWindowSeconds = 0, HangThresholdSeconds = 0, StartupGraceSeconds = 0 };
        var errors = s.Validate();
        Check.That(errors).HasElementThatMatches(e => e.Contains(nameof(SupervisionSettings.MaxRetries)));
        Check.That(errors).HasElementThatMatches(e => e.Contains(nameof(SupervisionSettings.CrashWindowSeconds)));
        Check.That(errors).HasElementThatMatches(e => e.Contains(nameof(SupervisionSettings.HangThresholdSeconds)));
        Check.That(errors).HasElementThatMatches(e => e.Contains(nameof(SupervisionSettings.StartupGraceSeconds)));
    }

    [Fact]
    public void Validate_InRange_ReturnsNoErrors()
    {
        var s = new SupervisionSettings();

        Check.That(s.Validate()).IsEmpty();
    }
}
