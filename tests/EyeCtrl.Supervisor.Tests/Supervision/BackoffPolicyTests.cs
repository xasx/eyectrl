namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class BackoffPolicyTests
{
    [Fact]
    public void Curve_IsImmediate_5_15_60_60()
    {
        var b = new BackoffPolicy();
        Check.That(b.DelayFor(consecutiveFailures: 1)).IsEqualTo(TimeSpan.Zero);
        Check.That(b.DelayFor(2)).IsEqualTo(TimeSpan.FromSeconds(5));
        Check.That(b.DelayFor(3)).IsEqualTo(TimeSpan.FromSeconds(15));
        Check.That(b.DelayFor(4)).IsEqualTo(TimeSpan.FromSeconds(60));
        Check.That(b.DelayFor(5)).IsEqualTo(TimeSpan.FromSeconds(60)); // capped
    }
}
