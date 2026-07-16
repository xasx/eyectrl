namespace EyeCtrl.App.Tests.Startup;

public class PausedArgTests
{
    [Fact]
    public void IsPausedLaunch_DefaultsFalse()
    {
        Check.That(EyeCtrl.App.App.IsPausedLaunch).IsFalse();
    }
}
