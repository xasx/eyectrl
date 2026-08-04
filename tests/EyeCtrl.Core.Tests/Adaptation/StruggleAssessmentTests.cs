namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

public class StruggleAssessmentTests
{
    [Fact]
    public void SignalScore_None_IsZeroAndUngated()
    {
        var none = SignalScore.None;
        Check.That(none.Score).IsEqualTo(0.0);
        Check.That(none.Confidence).IsEqualTo(0.0);
        Check.That(none.GatePassed).IsFalse();
    }

    [Fact]
    public void FailureMode_DefinesTheSixResponseModes()
    {
        // Locks the §7.1/§13.2 mode set so the monitor and controller can rely on it. SignalNoise was split
        // out of SustainFailure in §13.3 so the smoothing response is driven only by noise evidence.
        var modes = Enum.GetValues<FailureMode>();
        Check.That(modes).HasSize(6);
        Check.That(modes).Contains(
            FailureMode.SustainFailure,
            FailureMode.AccidentalActivation,
            FailureMode.Aiming,
            FailureMode.Drift,
            FailureMode.PoorTracking,
            FailureMode.SignalNoise);
    }

    [Fact]
    public void StruggleAssessment_None_IsEmpty()
    {
        Check.That(StruggleAssessment.None.Modes).IsEmpty();
        Check.That(StruggleAssessment.None.AggregateLevel).IsEqualTo(0.0);
    }

    [Fact]
    public void ForMode_ReturnsMatchingEntry_OrNull()
    {
        // Positional-record named args are PascalCase (Mode/Score/Confidence/GatePassed/Context).
        var sustain = new FailureModeAssessment(
            FailureMode.SustainFailure, Score: 0.7, Confidence: 1.0, GatePassed: true, Context: null);
        var aiming = new FailureModeAssessment(
            FailureMode.Aiming, Score: 0.4, Confidence: 0.8, GatePassed: false, Context: DwellContext.ActionMenu);
        var assessment = new StruggleAssessment(new[] { sustain, aiming }, aggregateLevel: 0.7);

        Check.That(assessment.ForMode(FailureMode.SustainFailure).HasValue).IsTrue();
        Check.That(assessment.ForMode(FailureMode.SustainFailure)!.Value.Score).IsEqualTo(0.7);
        Check.That(assessment.ForMode(FailureMode.Aiming)!.Value.Context).IsEqualTo(DwellContext.ActionMenu);
        Check.That(assessment.ForMode(FailureMode.Drift).HasValue).IsFalse();
    }
}
