namespace EyeCtrl.App.Tests.Services;

using System;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using NFluent;

public class AdaptationCueServiceTests
{
    private sealed class FakeCuePresenter : ICuePresenter
    {
        public int ShowCalls { get; private set; }
        public int HideCalls { get; private set; }
        public double LastIntensity { get; private set; } = double.NaN;
        public void Show(double intensity) { ShowCalls++; LastIntensity = intensity; }
        public void Hide() { HideCalls++; }
    }

    private sealed class FakeFeedbackGate : IFeedbackGate
    {
        public bool IsFeedbackSuppressed { get; set; }
    }

    // Builds an assessment with a given aggregate level (Modes are irrelevant to the cue, which reads
    // only AggregateLevel). StruggleAssessment ctor is (IReadOnlyList<FailureModeAssessment>, double).
    private static StruggleAssessment Level(double aggregate) =>
        new(Array.Empty<FailureModeAssessment>(), aggregate);

    // Controllable monotonic clock so the flash throttle can be exercised without waiting.
    private sealed class FakeClock
    {
        public long NowMs { get; set; }
        public long Read() => NowMs;
    }

    private static (AdaptationCueService Svc, AdaptationSettings Adaptation, FakeFeedbackGate Gate, FakeCuePresenter Presenter) Make()
    {
        var settings = new EyeCtrlSettings();     // ShowCue defaults true (1b-②)
        var gate = new FakeFeedbackGate();
        var presenter = new FakeCuePresenter();
        var svc = new AdaptationCueService(settings.Adaptation, gate, presenter);
        return (svc, settings.Adaptation, gate, presenter);
    }

    private static (AdaptationCueService Svc, AdaptationSettings Adaptation, FakeFeedbackGate Gate, FakeCuePresenter Presenter, FakeClock Clock) MakeWithClock()
    {
        var settings = new EyeCtrlSettings();
        var gate = new FakeFeedbackGate();
        var presenter = new FakeCuePresenter();
        var clock = new FakeClock { NowMs = 10_000 }; // non-zero start: a 0 clock would hide off-by-one bugs
        var svc = new AdaptationCueService(settings.Adaptation, gate, presenter, clock.Read);
        return (svc, settings.Adaptation, gate, presenter, clock);
    }

    [Fact]
    public void AllowAllFeedbackGate_NeverSuppresses()
    {
        Check.That(new AllowAllFeedbackGate().IsFeedbackSuppressed).IsFalse();
    }

    [Fact]
    public void ComputeCueState_VisibleWithIntensityFromAggregate()
    {
        var s = AdaptationCueService.ComputeCueState(Level(0.6), showCue: true, feedbackSuppressed: false);
        Check.That(s.Visible).IsTrue();
        Check.That(s.Intensity).IsEqualTo(0.6);
    }

    [Fact]
    public void ComputeCueState_HiddenWhenLevelZero()
    {
        var s = AdaptationCueService.ComputeCueState(StruggleAssessment.None, showCue: true, feedbackSuppressed: false);
        Check.That(s.Visible).IsFalse();
        Check.That(s.Intensity).IsEqualTo(0.0);
    }

    [Fact]
    public void ComputeCueState_HiddenWhenShowCueOff()
    {
        var s = AdaptationCueService.ComputeCueState(Level(0.9), showCue: false, feedbackSuppressed: false);
        Check.That(s.Visible).IsFalse();
    }

    [Fact]
    public void ComputeCueState_HiddenWhenSuppressed()
    {
        var s = AdaptationCueService.ComputeCueState(Level(0.9), showCue: true, feedbackSuppressed: true);
        Check.That(s.Visible).IsFalse();
    }

    [Fact]
    public void OnAssessment_ShowsPresenter_WhenVisible()
    {
        var (svc, _, _, presenter) = Make();
        var state = svc.OnAssessment(Level(0.5));
        Check.That(state.Visible).IsTrue();
        Check.That(presenter.ShowCalls).IsEqualTo(1);
        Check.That(presenter.LastIntensity).IsEqualTo(0.5);
    }

    [Fact]
    public void OnAssessment_DoesNotShow_WhenShowCueOff()
    {
        var (svc, adaptation, _, presenter) = Make();
        adaptation.ShowCue = false;
        var state = svc.OnAssessment(Level(0.5));
        Check.That(state.Visible).IsFalse();
        Check.That(presenter.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void OnAssessment_DoesNotShow_WhenSuppressed()
    {
        var (svc, _, gate, presenter) = Make();
        gate.IsFeedbackSuppressed = true;
        var state = svc.OnAssessment(Level(0.5));
        Check.That(state.Visible).IsFalse();
        Check.That(presenter.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void OnAssessment_DoesNotShow_WhenLevelZero()
    {
        var (svc, _, _, presenter) = Make();
        svc.OnAssessment(StruggleAssessment.None);
        Check.That(presenter.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void OnAssessment_HidesPresenter_WhenDecisionFlipsToHidden()
    {
        // PR #71 review #6: when the cue decision flips from visible to hidden MID-STRUGGLE — e.g. feedback
        // suppression turning on while struggle is ongoing (§8.10) — the badge must be force-hidden now,
        // not left flashing through its 2 s auto-fade window in a sensitive context. Pre-fix OnAssessment
        // only called Show when visible and did nothing when hidden, so a mid-struggle suppress-onset left
        // the badge up until struggle stopped. Now the hidden path calls Hide.
        var (svc, _, gate, presenter) = Make();
        svc.OnAssessment(Level(0.7));            // visible → Show
        Check.That(presenter.ShowCalls).IsEqualTo(1);
        Check.That(presenter.HideCalls).IsEqualTo(0);

        gate.IsFeedbackSuppressed = true;        // suppress mid-struggle → decision flips to hidden
        svc.OnAssessment(Level(0.7));
        Check.That(presenter.HideCalls).IsEqualTo(1); // force-hidden immediately
        Check.That(presenter.ShowCalls).IsEqualTo(1); // not re-shown
    }

    [Fact]
    public void OnAssessment_ThrottlesReflash_WithinTheFadeWindow()
    {
        // The coordinator raises an assessment every 250 ms while struggle persists. Re-flashing on each one
        // meant the badge never completed its ~2 s fade and cost a topmost SetWindowPos plus a new WPF
        // DoubleAnimation per tick, on the thread that also renders the gaze cursor. Only the leading edge
        // should flash within one fade window.
        var (svc, _, _, presenter, clock) = MakeWithClock();

        for (int tick = 0; tick < 8; tick++) // 8 ticks = 2 s at the 250 ms cadence
        {
            svc.OnAssessment(Level(0.6));
            clock.NowMs += 250;
        }

        Check.That(presenter.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void OnAssessment_ReflashesOncePerFadeWindow()
    {
        // Struggle that persists past a full fade re-pulses, so the badge keeps signalling rather than
        // vanishing for the rest of the episode.
        var (svc, _, _, presenter, clock) = MakeWithClock();

        svc.OnAssessment(Level(0.6));                 // leading edge
        Check.That(presenter.ShowCalls).IsEqualTo(1);

        clock.NowMs += AdaptationCueService.ReflashIntervalMs - 1;
        svc.OnAssessment(Level(0.6));                 // still inside the window
        Check.That(presenter.ShowCalls).IsEqualTo(1);

        clock.NowMs += 1;                             // window elapsed exactly
        svc.OnAssessment(Level(0.6));
        Check.That(presenter.ShowCalls).IsEqualTo(2);
    }

    [Fact]
    public void OnAssessment_ReflashesImmediately_WhenIntensityEscalates()
    {
        // Worsening struggle is worth an immediate pulse at the higher peak opacity rather than waiting out
        // the throttle — the badge's intensity is the whole signal.
        var (svc, _, _, presenter, clock) = MakeWithClock();

        svc.OnAssessment(Level(0.40));
        Check.That(presenter.ShowCalls).IsEqualTo(1);

        clock.NowMs += 250;
        svc.OnAssessment(Level(0.40 + AdaptationCueService.IntensityEscalationDelta));
        Check.That(presenter.ShowCalls).IsEqualTo(2);
        Check.That(presenter.LastIntensity).IsEqualTo(0.40 + AdaptationCueService.IntensityEscalationDelta);
    }

    [Fact]
    public void OnAssessment_DoesNotReflash_WhenIntensityDrifts_BelowEscalation()
    {
        // A small wobble in the aggregate is not an escalation; only a material rise beats the throttle.
        var (svc, _, _, presenter, clock) = MakeWithClock();

        svc.OnAssessment(Level(0.40));
        clock.NowMs += 250;
        svc.OnAssessment(Level(0.45));
        clock.NowMs += 250;
        svc.OnAssessment(Level(0.30));

        Check.That(presenter.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void OnAssessment_NewEpisodeFlashesImmediately_AfterTheCueWentDown()
    {
        // The throttle is per-episode, not a global rate limit: once the badge has been taken down, the next
        // episode's leading edge flashes at once however recently the last pulse was.
        var (svc, _, gate, presenter, clock) = MakeWithClock();

        svc.OnAssessment(Level(0.6));
        Check.That(presenter.ShowCalls).IsEqualTo(1);

        gate.IsFeedbackSuppressed = true;   // decision flips to hidden → badge taken down
        clock.NowMs += 250;
        svc.OnAssessment(Level(0.6));
        Check.That(presenter.HideCalls).IsEqualTo(1);

        gate.IsFeedbackSuppressed = false;  // new episode, well inside the throttle window
        clock.NowMs += 250;
        svc.OnAssessment(Level(0.6));
        Check.That(presenter.ShowCalls).IsEqualTo(2);
    }

    [Fact]
    public void OnAssessment_DoesNotRepeatHide_WhileGatedOff()
    {
        // CueOverlayWindow.Hide is two dependency-property writes plus a generation bump; with the cue gated
        // off there is nothing on screen to take down, so a struggle that persists must not re-issue it 4×/s.
        var (svc, adaptation, _, presenter, clock) = MakeWithClock();
        adaptation.ShowCue = false;

        for (int tick = 0; tick < 8; tick++)
        {
            svc.OnAssessment(Level(0.6));
            clock.NowMs += 250;
        }

        Check.That(presenter.ShowCalls).IsEqualTo(0);
        Check.That(presenter.HideCalls).IsEqualTo(0);
    }
}
