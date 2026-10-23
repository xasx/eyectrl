namespace EyeCtrl.App.Tests.Services;

using System;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
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
        public bool Visible { get; set; }
        public void ShowDriftNotice() => ShowCalls++;

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
}
