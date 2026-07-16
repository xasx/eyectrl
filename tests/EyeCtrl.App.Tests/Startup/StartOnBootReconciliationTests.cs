namespace EyeCtrl.App.Tests.Startup;

using EyeCtrl.App.Startup;

public class StartOnBootReconciliationTests
{
    [Theory]
    [InlineData(true, true, false)]   // supervised + StartOnBoot on  -> never self-register
    [InlineData(true, false, false)]  // supervised + StartOnBoot off -> never self-register
    [InlineData(false, true, true)]   // standalone + StartOnBoot on  -> register
    [InlineData(false, false, false)] // standalone + StartOnBoot off -> don't register
    public void DecidesSelfRegistration(bool supervised, bool startOnBoot, bool expectRegister)
    {
        Check.That(StartOnBootPolicy.ShouldSelfRegister(supervised, startOnBoot)).IsEqualTo(expectRegister);
    }
}
