namespace EyeCtrl.App.Tests.Services;

using System;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using NFluent;

public class DriftNoticeServiceTests
{
    private sealed class FakeFeedbackGate : IFeedbackGate
    {
        public bool IsFeedbackSuppressed { get; set; }
    }

    private sealed class FakeDriftNoticePresenter : IDriftNoticePresenter
    {
        public int ShowCalls { get; private set; }
        public int WithdrawCalls { get; private set; }
        public bool Visible { get; set; }
        public void ShowDriftNotice() => ShowCalls++;

        public void WithdrawDriftNotice() => WithdrawCalls++;

        public bool TryGetVisibleBounds(out EyeCtrl.Core.Models.PhysicalRect bounds)
        {
            bounds = default;
            return Visible;
        }
    }

    // Assessment with Drift gated (or not) — the service keys off ForMode(Drift).GatePassed.
    private static StruggleAssessment Drift(bool gated) => new(
        new[] { new FailureModeAssessment(FailureMode.Drift, Score: 0.6, Confidence: 1.0, GatePassed: gated, Context: null) },
        gated ? 0.6 : 0.0);

    private static StruggleAssessment NoDrift() => StruggleAssessment.None;

    private static (DriftNoticeService Svc, FakeFeedbackGate Gate, FakeDriftNoticePresenter Presenter) Make()
    {
        var gate = new FakeFeedbackGate();
        var presenter = new FakeDriftNoticePresenter();
        return (new DriftNoticeService(gate, presenter), gate, presenter);
    }

    /// <summary>A minimal real coordinator: <see cref="DriftNoticeService.Start"/> only subscribes to
    /// its assessment stream, and the runtime edge is what the tests drive.</summary>
    private static AdaptationCoordinator MakeCoordinator()
    {
        var settings = new EyeCtrlSettings();
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        return new AdaptationCoordinator(
            new StruggleMonitor(new BufferingTelemetrySink(capacity: 16), settings.EyeCare),
            new AdaptationController(provider, settings.Adaptation),
            new AdaptationLearner(provider, settings.Adaptation),
            settings.Adaptation, new TestFakes.FakeGazeProvider(), new InteractionManager());
    }

    [Fact]
    public void DriftGateOnset_ShowsNoticeOnce()
    {
        var (svc, _, presenter) = Make();
        Check.That(svc.OnAssessment(Drift(gated: true))).IsTrue();
        Check.That(presenter.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void SustainedDriftGate_DoesNotReShow()
    {
        var (svc, _, presenter) = Make();
        svc.OnAssessment(Drift(gated: true));   // shows
        svc.OnAssessment(Drift(gated: true));   // same episode
        svc.OnAssessment(Drift(gated: true));
        Check.That(presenter.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void NewDriftEpisode_ShowsAgain()
    {
        var (svc, _, presenter) = Make();
        svc.OnAssessment(Drift(gated: true));   // episode 1 → shows
        svc.OnAssessment(Drift(gated: false));  // episode ends (re-arm)
        Check.That(svc.OnAssessment(Drift(gated: true))).IsTrue(); // episode 2 → shows again
        Check.That(presenter.ShowCalls).IsEqualTo(2);
    }

    [Fact]
    public void SensitiveContext_SuppressesTheNotice()
    {
        var (svc, gate, presenter) = Make();
        gate.IsFeedbackSuppressed = true;
        Check.That(svc.OnAssessment(Drift(gated: true))).IsFalse();
        Check.That(presenter.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void NoDrift_ShowsNothing()
    {
        var (svc, _, presenter) = Make();
        svc.OnAssessment(NoDrift());
        svc.OnAssessment(Drift(gated: false));
        Check.That(presenter.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void SuppressionAtOnset_BlocksNoticeForEntireEpisode_EvenIfSuppressionLifts()
    {
        // §8.10-faithful trade: if a drift episode starts while feedback is suppressed, marking the
        // episode handled BEFORE the suppression check means no notice shows for this episode AT ALL —
        // even if suppression lifts mid-episode. Never leak that drift was detected during the sensitive
        // window. A fresh episode (after Drift clears and re-gates) re-arms.
        var (svc, gate, presenter) = Make();
        gate.IsFeedbackSuppressed = true;
        svc.OnAssessment(Drift(gated: true));   // onset suppressed → episode marked handled, no notice
        gate.IsFeedbackSuppressed = false;       // suppression lifts mid-episode
        svc.OnAssessment(Drift(gated: true));    // still same episode → no notice
        Check.That(presenter.ShowCalls).IsEqualTo(0);

        svc.OnAssessment(Drift(gated: false));   // episode ends → re-arm
        Check.That(svc.OnAssessment(Drift(gated: true))).IsTrue(); // fresh episode → shows
        Check.That(presenter.ShowCalls).IsEqualTo(1);
    }

    // B4 fixup-01 H1: the slot may hold a notice past the runtime (a caregiver alert can stay up
    // for as long as its condition is sustained), so the service's teardown must withdraw it.
    [Fact]
    public void Dispose_WithdrawsTheNoticeFromTheSlot()
    {
        var (svc, _, presenter) = Make();
        svc.OnAssessment(Drift(gated: true));
        Check.That(presenter.WithdrawCalls).IsEqualTo(0);

        svc.Dispose();

        Check.WithCustomMessage("a queued notice must not survive the service that raised it")
            .That(presenter.WithdrawCalls).IsEqualTo(1);
    }

    // B4 fixup-01 H1: the re-validation predicate the presenter reads when the slot reaches a
    // queued notice. All three clauses are load-bearing: running, not suppressed, episode current.
    [Fact]
    public void CanShowNotice_RequiresRunning_NoSuppression_AndACurrentEpisode()
    {
        var (svc, gate, _) = Make();
        Check.WithCustomMessage("no episode has been raised yet").That(svc.CanShowNotice).IsFalse();

        svc.OnAssessment(Drift(gated: true));
        Check.WithCustomMessage("a current, unsuppressed episode may show").That(svc.CanShowNotice).IsTrue();

        gate.IsFeedbackSuppressed = true;
        Check.WithCustomMessage("§8.10: suppressed feedback may never show").That(svc.CanShowNotice).IsFalse();
        gate.IsFeedbackSuppressed = false;

        svc.OnAssessment(Drift(gated: false));
        Check.WithCustomMessage("an ended episode may never show late").That(svc.CanShowNotice).IsFalse();

        svc.OnAssessment(Drift(gated: true)); // a fresh episode re-arms
        Check.That(svc.CanShowNotice).IsTrue();

        svc.Dispose();
        Check.WithCustomMessage("the runtime stopped: a queued notice may not show").That(svc.CanShowNotice).IsFalse();

        // A late assessment delivered after the unsubscribe still must not reopen the runtime:
        // the stopped flag is the guard, independent of the per-episode state.
        svc.OnAssessment(Drift(gated: true));
        Check.WithCustomMessage("a stopped runtime must stay closed even if a late assessment arrives")
            .That(svc.CanShowNotice).IsFalse();
    }

    // B4 fixup-02 M2 (kills N6): Start must reopen the runtime it closed in Dispose. Without the
    // reset, every drift notice for the rest of the session is silently dropped after one off→on.
    [Fact]
    public void ReEnable_AfterDispose_ShowsAFreshEpisodeAgain()
    {
        var (svc, _, presenter) = Make();
        var coordinator = MakeCoordinator();
        svc.Start(coordinator);

        Check.That(svc.OnAssessment(Drift(gated: true))).IsTrue();
        Check.That(svc.CanShowNotice).IsTrue();

        svc.Dispose();
        Check.WithCustomMessage("disabled").That(svc.CanShowNotice).IsFalse();

        svc.Start(coordinator);
        Check.WithCustomMessage("a re-enable alone is not an episode").That(svc.CanShowNotice).IsFalse();

        Check.WithCustomMessage("a fresh episode after the re-enable must show again")
            .That(svc.OnAssessment(Drift(gated: true))).IsTrue();
        Check.WithCustomMessage("the reopened runtime must let the notice through")
            .That(svc.CanShowNotice).IsTrue();
        Check.That(presenter.ShowCalls).IsEqualTo(2);
    }

    // B4 fixup-02 L2 (kills N5): Dispose ENDS the drift episode as well as stopping the runtime, so
    // a re-enable alone cannot revive the old episode's notice.
    [Fact]
    public void Dispose_EndsTheEpisode_SoAReEnableAloneDoesNotReviveIt()
    {
        var (svc, _, _) = Make();
        var coordinator = MakeCoordinator();
        svc.Start(coordinator);
        svc.OnAssessment(Drift(gated: true));
        Check.That(svc.CanShowNotice).IsTrue();

        svc.Dispose();
        svc.Start(coordinator); // runtime back on, but the disable ended the episode

        Check.WithCustomMessage("the disable must end the episode, not leave it to be revived by Start")
            .That(svc.CanShowNotice).IsFalse();
    }
}
