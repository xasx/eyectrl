namespace EyeCtrl.App.Tests.Services;

using System;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using NFluent;

public class AdaptationLogTests
{
    private sealed class FakeFeedbackGate : IFeedbackGate
    {
        public bool IsFeedbackSuppressed { get; set; }
    }

    // Builds an assessment whose only gated mode(s) are the ones passed (Score/Confidence are irrelevant
    // to the log, which keys off GatePassed). An empty list = no struggle (idle).
    private static StruggleAssessment Gated(params FailureMode[] modes)
    {
        var list = new FailureModeAssessment[modes.Length];
        for (int i = 0; i < modes.Length; i++)
            list[i] = new FailureModeAssessment(modes[i], Score: 0.7, Confidence: 1.0, GatePassed: true, Context: null);
        double aggregate = modes.Length == 0 ? 0.0 : 0.7;
        return new StruggleAssessment(list, aggregate);
    }

    private static (AdaptationLog Log, AdaptationSettings Adaptation, FakeFeedbackGate Gate) Make()
    {
        var settings = new EyeCtrlSettings();
        var gate = new FakeFeedbackGate();
        return (new AdaptationLog(settings.Adaptation, gate), settings.Adaptation, gate);
    }

    [Fact]
    public void IdleTicks_LogNothing()
    {
        var (log, _, _) = Make();
        Check.That(log.OnAssessment(StruggleAssessment.None)).IsEqualTo(AdaptationLogAction.None);
        Check.That(log.OnAssessment(Gated())).IsEqualTo(AdaptationLogAction.None); // still no gated modes
    }

    [Fact]
    public void StruggleOnset_IsLogged()
    {
        var (log, _, _) = Make();
        Check.That(log.OnAssessment(Gated(FailureMode.SustainFailure))).IsEqualTo(AdaptationLogAction.Logged);
    }

    [Fact]
    public void SameGatedSet_IsNotReLogged()
    {
        var (log, _, _) = Make();
        log.OnAssessment(Gated(FailureMode.SustainFailure));                          // Logged
        Check.That(log.OnAssessment(Gated(FailureMode.SustainFailure)))               // unchanged set
            .IsEqualTo(AdaptationLogAction.None);
    }

    [Fact]
    public void AddingAMode_IsLogged()
    {
        var (log, _, _) = Make();
        log.OnAssessment(Gated(FailureMode.SustainFailure));                          // Logged
        Check.That(log.OnAssessment(Gated(FailureMode.SustainFailure, FailureMode.Drift)))
            .IsEqualTo(AdaptationLogAction.Logged);                                   // set grew
    }

    [Fact]
    public void StruggleClearing_IsLogged()
    {
        var (log, _, _) = Make();
        log.OnAssessment(Gated(FailureMode.SustainFailure));                          // Logged
        Check.That(log.OnAssessment(Gated())).IsEqualTo(AdaptationLogAction.Logged);  // back to empty = a transition
        Check.That(log.OnAssessment(Gated())).IsEqualTo(AdaptationLogAction.None);    // stays empty
    }

    [Fact]
    public void Transition_InSensitiveContext_LogsCoarseMarkerOnly()
    {
        var (log, _, gate) = Make();
        gate.IsFeedbackSuppressed = true;
        Check.That(log.OnAssessment(Gated(FailureMode.SustainFailure)))
            .IsEqualTo(AdaptationLogAction.SuppressedMarker);
    }
}
