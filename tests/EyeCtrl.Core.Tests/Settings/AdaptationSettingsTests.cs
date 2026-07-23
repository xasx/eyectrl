namespace EyeCtrl.Core.Tests.Settings;

using System.Linq;
using EyeCtrl.Core.Settings;

public class AdaptationSettingsTests
{
    [Fact]
    public void Defaults_AreOptInAndConservative()
    {
        var s = new AdaptationSettings();
        Check.That(s.Enabled).IsFalse();
        Check.That(s.Posture).IsEqualTo(AdaptationPosture.Conservative);
        Check.That(s.Validate()).IsEmpty();
    }

    [Fact]
    public void EyeCtrlSettings_EnumeratesAdaptation()
    {
        var names = new EyeCtrlSettings().EnumerateSubSettings().Select(x => x.Name).ToList();
        Check.That(names).Contains("Adaptation");
    }
}
