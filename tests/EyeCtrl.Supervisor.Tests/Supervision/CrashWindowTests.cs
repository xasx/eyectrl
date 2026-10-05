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
        Check.That(w.ShouldEscalate(Base.AddSeconds(30), consecutiveFailures: 3)).IsFalse();
    }

    [Fact]
    public void ShouldEscalate_True_AtMaxRetriesPlusOne()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        for (int i = 0; i < 4; i++) w.RecordStrike(Base.AddSeconds(i * 10));
        Check.That(w.ShouldEscalate(Base.AddSeconds(35), consecutiveFailures: 4)).IsTrue();
    }

    [Fact]
    public void OldStrikes_ExpireOutOfWindow()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        for (int i = 0; i < 3; i++) w.RecordStrike(Base.AddSeconds(i * 10));
        // 200s later — all strikes aged out
        Check.That(w.ShouldEscalate(Base.AddSeconds(200), consecutiveFailures: 3)).IsFalse();
    }

    [Fact]
    public void Reset_ClearsStrikes()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        w.RecordStrike(Base);
        w.Reset();
        Check.That(w.ShouldEscalate(Base, consecutiveFailures: 0)).IsFalse();
    }

    [Fact]
    public void UpdateLimits_AppliesNewMaxRetries_GoingForward()
    {
        // A live settings edit (supervisor setup window) must change escalation without a restart.
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        w.UpdateLimits(maxRetries: 1, windowSeconds: 120);
        w.RecordStrike(Base);
        Check.That(w.ShouldEscalate(Base, consecutiveFailures: 1)).IsFalse();          // 1 strike == maxRetries(1), permitted
        w.RecordStrike(Base.AddSeconds(1));
        Check.That(w.ShouldEscalate(Base.AddSeconds(1), consecutiveFailures: 2)).IsTrue(); // 2 strikes > 1 -> escalate
    }

    [Fact]
    public void ShouldEscalate_True_ConsecutiveBeyondMaxRetries_EvenWhenAllStrikesAgedOut()
    {
        // Ticket 06-C: a crash loop slower than the window (a 60s grace per failed relaunch, or a
        // crash every ~100s) never has more than MaxRetries strikes inside the window, so the
        // window rule alone would never escalate and the user would be stuck in a kill-relaunch
        // loop. Consecutive failures since the last survived window must escalate too.
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        w.RecordStrike(Base);
        w.RecordStrike(Base.AddSeconds(100));
        w.RecordStrike(Base.AddSeconds(200));
        w.RecordStrike(Base.AddSeconds(300));

        // At +301s the window (120s) holds only the last strike, but four in a row is past the limit.
        Check.That(w.ShouldEscalate(Base.AddSeconds(301), consecutiveFailures: 4)).IsTrue();
    }

    [Fact]
    public void ShouldEscalate_False_ConsecutiveAtTheLimit()
    {
        var w = new CrashWindow(maxRetries: 3, windowSeconds: 120);
        Check.That(w.ShouldEscalate(Base, consecutiveFailures: 3)).IsFalse();
    }
}
