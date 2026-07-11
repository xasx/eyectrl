namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class TrackStatusSettingsDefaultsTests
{
    [Fact]
    public void Default_DisabledAndValidates()
    {
        var s = new TrackStatusSettings();
        Check.That(s.Enabled).IsFalse();
        Check.That(s.Validate()).IsEmpty();
    }
}
