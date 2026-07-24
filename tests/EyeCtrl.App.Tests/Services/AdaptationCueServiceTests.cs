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
        public double LastIntensity { get; private set; } = double.NaN;
        public void Show(double intensity) { ShowCalls++; LastIntensity = intensity; }
    }

    private sealed class FakeFeedbackGate : IFeedbackGate
    {
        public bool IsFeedbackSuppressed { get; set; }
    }

    // Builds an assessment with a given aggregate level (Modes are irrelevant to the cue, which reads
    // only AggregateLevel). StruggleAssessment ctor is (IReadOnlyList<FailureModeAssessment>, double).
    private static StruggleAssessment Level(double aggregate) =>
        new(Array.Empty<FailureModeAssessment>(), aggregate);

    private static (AdaptationCueService Svc, AdaptationSettings Adaptation, FakeFeedbackGate Gate, FakeCuePresenter Presenter) Make()
    {
        var settings = new EyeCtrlSettings();     // ShowCue defaults true (1b-②)
        var gate = new FakeFeedbackGate();
        var presenter = new FakeCuePresenter();
        var svc = new AdaptationCueService(settings.Adaptation, gate, presenter);
        return (svc, settings.Adaptation, gate, presenter);
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
}
