namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class CaregiverAlertServiceTests
{
    private sealed class FakeFeedbackGate : IFeedbackGate
    {
        public bool IsFeedbackSuppressed { get; set; }
    }

    private sealed class FakePresenter : ICaregiverAlertPresenter
    {
        public List<CaregiverAlertCondition> Shows { get; } = new();
        public int FadeCalls { get; private set; }
        public int HideCalls { get; private set; }
        public void ShowCaregiverAlert(CaregiverAlertCondition condition) => Shows.Add(condition);
        public void FadeOutCaregiverAlert() => FadeCalls++;
        public void HideCaregiverAlert() => HideCalls++;
    }

    // Aggregate-only struggle (no modes): the struggle condition keys off AggregateLevel alone.
    private static StruggleAssessment Struggle(double level) =>
        new(Array.Empty<FailureModeAssessment>(), level);

    // Mirrors the monitor: a gated PoorTracking mode's score feeds the aggregate, so this assessment
    // satisfies BOTH conditions — which is why the service evaluates poor tracking first (D4).
    private static StruggleAssessment PoorTracking(bool gated) => new(
        new[] { new FailureModeAssessment(FailureMode.PoorTracking, Score: 0.6, Confidence: 1.0, GatePassed: gated, Context: null) },
        gated ? 0.6 : 0.0);

    private sealed class Clock
    {
        public long Now;
    }

    private static (CaregiverAlertService Svc, EyeCtrlSettings Settings, FakeFeedbackGate Gate, FakePresenter Presenter, List<CaregiverAlertCondition> Logged, Clock Clock) Make()
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.CaregiverAlerts = true;
        var gate = new FakeFeedbackGate();
        var presenter = new FakePresenter();
        var logged = new List<CaregiverAlertCondition>();
        var clock = new Clock();
        return (new CaregiverAlertService(settings.Adaptation, gate, presenter, logged.Add, () => clock.Now),
            settings, gate, presenter, logged, clock);
    }

    // Drives `ticks` assessments 250 ms apart from the clock's current value.
    private static void Run(CaregiverAlertService svc, Clock clock, StruggleAssessment assessment, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            clock.Now += 250;
            svc.OnAssessment(assessment);
        }
    }

    [Fact]
    public void Disabled_NoOnset_EvenAfterLongSustain()
    {
        var (svc, settings, _, presenter, _, clock) = Make();
        settings.Adaptation.CaregiverAlerts = false;
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.8), ticks: 600); // 150 s of sustained struggle
        Check.That(presenter.Shows).IsEmpty();
    }

    [Fact]
    public void StruggleSustained_SixtySeconds_FiresOnceAndLogs()
    {
        var (svc, _, _, presenter, logged, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // the 241st tick reaches exactly 60.00 s of sustain
        Check.That(presenter.Shows).ContainsExactly(CaregiverAlertCondition.SustainedStruggle);
        Check.That(logged).ContainsExactly(CaregiverAlertCondition.SustainedStruggle);
        Run(svc, clock, Struggle(0.6), ticks: 40); // still the same episode — once per episode
        Check.That(presenter.Shows).HasSize(1);
    }

    [Fact]
    public void Onset_AtExactlySixtySeconds_NotAtFiftyNine()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 240); // 60.0 s of ticks, 59.75 s of sustain
        Check.That(presenter.Shows).IsEmpty();
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.6)); // sustain reaches exactly 60 s
        Check.That(presenter.Shows).HasSize(1);
    }

    [Fact]
    public void SustainBrokenByDipBelowThreshold_Restarts()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 120); // 30 s
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.4)); // below 0.5 → the continuous hold breaks
        Run(svc, clock, Struggle(0.6), ticks: 240); // 59.75 s of the NEW sustain — cumulative 90+ but not continuous
        Check.That(presenter.Shows).IsEmpty();
        Run(svc, clock, Struggle(0.6), ticks: 1); // the new sustain reaches exactly 60 s
        Check.That(presenter.Shows).HasSize(1);
    }

    [Fact]
    public void PoorTrackingGated_SixtySeconds_FiresPoorTracking()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, PoorTracking(gated: true), ticks: 241);
        // Aggregate is also 0.6 here — poor tracking wins because it is evaluated first (D4).
        Check.That(presenter.Shows).ContainsExactly(CaregiverAlertCondition.SustainedPoorTracking);
    }

    [Fact]
    public void PoorTrackingUngated_NeverFires()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, PoorTracking(gated: false), ticks: 600); // aggregate 0.0 → struggle unmet too
        Check.That(presenter.Shows).IsEmpty();
    }

    [Fact]
    public void RateLimit_SecondEpisodeWithinInterval_DoesNotFire()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // first onset fires
        Check.That(presenter.Shows).HasSize(1);
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.3)); // clear → the window fades
        clock.Now += 31_000; // a proper clear gap — the next episode is FRESH
        Run(svc, clock, Struggle(0.6), ticks: 241); // fully sustained again, ~92 s after the first onset
        Check.That(presenter.Shows).HasSize(1); // but inside the default 10-minute interval
        Check.That(presenter.FadeCalls).IsEqualTo(1);
    }

    [Fact]
    public void RateLimit_AfterIntervalAndClearGap_FiresAgain()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241);
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.3));
        clock.Now += 10 * 60_000 + 31_000; // interval elapsed AND clear gap satisfied
        Run(svc, clock, Struggle(0.6), ticks: 241);
        Check.That(presenter.Shows).HasSize(2);
    }

    [Fact]
    public void RateLimit_IntervalReadsTheLiveSetting()
    {
        var (svc, settings, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241);
        settings.Adaptation.CaregiverMinIntervalMinutes = 1;
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.3));
        clock.Now += 31_000; // clear gap only — ~92 s total since the first onset
        Run(svc, clock, Struggle(0.6), ticks: 241);
        Check.That(presenter.Shows).HasSize(2); // with the default 10 min this would still be blocked
    }

    [Fact]
    public void ReArm_RequiresThirtySecondClearGap()
    {
        var (svc, settings, _, presenter, _, clock) = Make();
        settings.Adaptation.CaregiverMinIntervalMinutes = 1; // take the interval out of the way
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fires
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.3)); // clear
        clock.Now += 10_000; // only 10 s clear — below the 30 s re-arm gap
        Run(svc, clock, Struggle(0.6), ticks: 241); // same flare-up: inherits "already alerted"
        Check.That(presenter.Shows).HasSize(1);
        svc.OnAssessment(Struggle(0.3));
        clock.Now += 31_000; // a proper 31 s clear
        Run(svc, clock, Struggle(0.6), ticks: 241);
        Check.That(presenter.Shows).HasSize(2);
    }

    [Fact]
    public void SuppressedOnset_Consumed_EvenAfterSuppressionLifts()
    {
        var (svc, _, gate, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 240); // 59.75 s — the NEXT tick is the onset
        gate.IsFeedbackSuppressed = true;
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.6)); // onset due, suppressed → consumed
        gate.IsFeedbackSuppressed = false;
        Run(svc, clock, Struggle(0.6), ticks: 100); // same episode → never shows (§8.10, D6)
        Check.That(presenter.Shows).IsEmpty();
        svc.OnAssessment(Struggle(0.3));
        clock.Now += 31_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fresh episode
        Check.That(presenter.Shows).HasSize(1);
    }

    [Fact]
    public void SuppressedOnset_DoesNotStartTheRateLimitClock()
    {
        var (svc, _, gate, presenter, _, clock) = Make(); // interval default: 10 min
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 240);
        gate.IsFeedbackSuppressed = true;
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.6)); // suppressed onset → consumed, NO clock start
        gate.IsFeedbackSuppressed = false;
        svc.OnAssessment(Struggle(0.3));
        clock.Now += 31_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // ≈1.5 min (91.5 s) after the suppressed onset
        Check.That(presenter.Shows).HasSize(1); // fires: had the suppressed onset armed the clock, this would block
    }

    [Fact]
    public void SuppressionMidDisplay_HidesImmediately()
    {
        var (svc, _, gate, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fires, window up
        gate.IsFeedbackSuppressed = true;
        svc.OnAssessment(Struggle(0.6)); // still sustained — suppression takes the window down AT ONCE
        Check.That(presenter.HideCalls).IsEqualTo(1);
        Check.That(presenter.FadeCalls).IsEqualTo(0);
    }

    [Fact]
    public void EpisodeClear_FadesTheWindowOut()
    {
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fires
        svc.OnAssessment(Struggle(0.3)); // natural clear → fade, not hide (D2)
        Check.That(presenter.FadeCalls).IsEqualTo(1);
        Check.That(presenter.HideCalls).IsEqualTo(0);
    }

    [Fact]
    public void NoneAssessment_EndsEpisode_SubsequentSustainStartsFresh()
    {
        var (svc, settings, _, presenter, _, clock) = Make();
        settings.Adaptation.CaregiverMinIntervalMinutes = 1; // keep the interval out of the way
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fires
        svc.OnAssessment(StruggleAssessment.None); // paused/disconnected → episode ends, window fades
        Check.That(presenter.FadeCalls).IsEqualTo(1);
        clock.Now += 31_000;
        Run(svc, clock, Struggle(0.6), ticks: 240); // 59.75 s of the fresh sustain — no fire yet
        Check.That(presenter.Shows).HasSize(1);
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.6));
        Check.That(presenter.Shows).HasSize(2);
    }

    [Fact]
    public void CaregiverToggledOffMidDisplay_HidesImmediately()
    {
        var (svc, settings, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 241); // fires, window up
        settings.Adaptation.CaregiverAlerts = false;
        svc.OnAssessment(Struggle(0.6)); // still sustained — feature-off takes the window down AT ONCE
        Check.That(presenter.HideCalls).IsEqualTo(1);
        Check.That(presenter.FadeCalls).IsEqualTo(0);
    }

    [Fact]
    public void Dispose_ClearsSustainState_NoStaleFireAfterRestart()
    {
        // Round-1 F2: the disable edge (StopRuntime) never raises a None — ResetMomentary sets
        // LastAssessment without invoking, and the timer is disposed synchronously on the
        // setting-change thread — so Dispose is the only place pre-disable sustain state can die.
        // Without the reset, the first post-restart tick below would fire instantly off the stale
        // SustainedSince (10 minutes old), banner + chime + JSONL after zero seconds of struggle.
        var (svc, _, _, presenter, _, clock) = Make();
        clock.Now = 1_000_000;
        Run(svc, clock, Struggle(0.6), ticks: 120); // 30 s sustained — below the onset threshold
        svc.Dispose(); // the disable edge
        clock.Now += 10 * 60_000; // a long disable
        Run(svc, clock, Struggle(0.6), ticks: 1); // first tick after re-enable: sustain must restart
        Check.That(presenter.Shows).IsEmpty();
        Run(svc, clock, Struggle(0.6), ticks: 239); // 59.75 s of the fresh sustain — still nothing
        Check.That(presenter.Shows).IsEmpty();
        clock.Now += 250;
        svc.OnAssessment(Struggle(0.6));
        Check.That(presenter.Shows).HasSize(1); // fires at the new episode's own 60 s
    }
}
