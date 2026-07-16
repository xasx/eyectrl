namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class CrashWindowTests
{
    // Worked example from spec: maxRetries=3 means 3 strikes permitted, escalate on 4th.
    private static readonly DateTime Base = new(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldEscalate_False_AtExactlyMaxRetries()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        for (int i = 0; i < 3; i++) w.RecordStrike(Base.AddSeconds(i * 10));
        Check.That(w.ShouldEscalate(Base.AddSeconds(30))).IsFalse();
    }

    [Fact]
    public void ShouldEscalate_True_AtMaxRetriesPlusOne()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        for (int i = 0; i < 4; i++) w.RecordStrike(Base.AddSeconds(i * 10));
        Check.That(w.ShouldEscalate(Base.AddSeconds(35))).IsTrue();
    }

    [Fact]
    public void OldStrikes_ExpireOutOfWindow()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        for (int i = 0; i < 3; i++) w.RecordStrike(Base.AddSeconds(i * 10));
        // 200s later — all strikes aged out
        Check.That(w.ShouldEscalate(Base.AddSeconds(200))).IsFalse();
    }

    [Fact]
    public void Reset_ClearsStrikes()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        w.RecordStrike(Base);
        w.Reset();
        Check.That(w.ShouldEscalate(Base)).IsFalse();
    }
}
