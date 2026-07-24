namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using NFluent;

public class AdaptationCueServiceTests
{
    [Fact]
    public void AllowAllFeedbackGate_NeverSuppresses()
    {
        Check.That(new AllowAllFeedbackGate().IsFeedbackSuppressed).IsFalse();
    }
}
