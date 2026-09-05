namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Processing;
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
        return (new AdaptationLog(settings.Adaptation, gate, new EffectiveSettingsProvider(settings.Adaptation)), settings.Adaptation, gate);
    }

    private static string TempLogPath()
        => Path.Combine(Path.GetTempPath(), "eyectrl_adaptlog_test_" + Guid.NewGuid() + ".jsonl");

    // A minimal real wiring (log + provider + coordinator pieces) so provider.Changed reaches the log
    // through the same subscription Start installs at runtime.
    private static (AdaptationLog Log, EffectiveSettingsProvider Provider, FakeFeedbackGate Gate, string JsonlPath) MakeWired(bool suppressed = false)
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        var gate = new FakeFeedbackGate { IsFeedbackSuppressed = suppressed };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var sink = new BufferingTelemetrySink(capacity: 64);
        var coordinator = new AdaptationCoordinator(
            new StruggleMonitor(sink, settings.EyeCare),
            new AdaptationController(provider, settings.Adaptation),
            new AdaptationLearner(provider, settings.Adaptation),
            settings.Adaptation, new TestFakes.FakeGazeProvider(), new InteractionManager());
        string path = TempLogPath();
        var log = new AdaptationLog(settings.Adaptation, gate, provider, jsonlPath: path);
        log.Start(coordinator);
        return (log, provider, gate, path);
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

    [Fact]
    public void LearnedChange_WritesJsonlEntry()
    {
        var (_, provider, _, path) = MakeWired();
        try
        {
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.2);
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.0); // "given back" — review round 1, finding 13
            string[] lines = File.ReadAllLines(path);
            Check.That(lines).HasSize(2);
            Check.That(lines[0]).Contains("\"kind\":\"learned\"");
            Check.That(lines[0]).Contains("\"knob\":\"DwellTime\""); // enum values serialize verbatim (PascalCase), like the modes array
            Check.That(lines[0]).Contains("\"new\":0.2");
            // A knob dropping to 0 leaves EnumerateLearnedFills, so the log must diff against the last
            // seen KEY SET as well — otherwise the decay-to-zero event (and the manual reset) vanish.
            Check.That(lines[1]).Contains("\"knob\":\"DwellTime\"");
            Check.That(lines[1]).Contains("\"old\":0.2");
            Check.That(lines[1]).Contains("\"new\":0");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ModeTransition_WritesJsonlEntry()
    {
        var (log, _, _, path) = MakeWired();
        try
        {
            log.OnAssessment(Gated(FailureMode.SustainFailure));
            string[] lines = File.ReadAllLines(path);
            Check.That(lines).HasSize(1);
            Check.That(lines[0]).Contains("\"kind\":\"modes\"");
            Check.That(lines[0]).Contains("\"modes\":[\"SustainFailure\"]");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MomentaryOnlyChange_WritesNoJsonlEntry()
    {
        var (_, provider, _, path) = MakeWired();
        try
        {
            provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.5, 0.0);
            Check.That(File.Exists(path)).IsFalse(); // momentary ramps are derivative of mode transitions
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SuppressedContext_MomentaryOnlyChange_WritesNothing()
    {
        // Review round 1, finding 11: the suppressed marker is written only when there was something to
        // suppress. Unconditionally, a password field + a 4 Hz momentary ramp would append 4 lines/s —
        // a timing trace of interaction during credential entry, the opposite of §8.10's intent.
        var (_, provider, _, path) = MakeWired(suppressed: true);
        try
        {
            provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.5, 0.0);
            Check.That(File.Exists(path)).IsFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SuppressedContext_WritesCoarseMarkerOnly()
    {
        var (log, provider, _, path) = MakeWired(suppressed: true);
        try
        {
            log.OnAssessment(Gated(FailureMode.SustainFailure));
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.2);
            string[] lines = File.ReadAllLines(path);
            Check.That(lines).HasSize(2);
            Check.That(lines[0]).Contains("\"kind\":\"suppressed\"");
            Check.That(lines[1]).Contains("\"kind\":\"suppressed\"");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SizeCap_RotatesToDotOne()
    {
        var (_, provider, _, path) = MakeWired();
        try
        {
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.2); // create the file
            // Push past the cap WITHOUT a 1 MB string alloc (finding 24): set the length directly.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
                fs.SetLength(AdaptationLog.MaxLogBytes + 1);
            provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.3); // next write rotates
            Check.That(File.Exists(path + ".1")).IsTrue();
            string[] lines = File.ReadAllLines(path);
            Check.That(lines).HasSize(1); // fresh file holds only the new entry
        }
        finally { File.Delete(path); File.Delete(path + ".1"); }
    }

    [Fact]
    public void NullPath_DisablesTheJsonlSide_Entirely()
    {
        // Self-contained (Make returns the 3-tuple the six existing facts destructure): a null path must
        // keep the Serilog behavior while no file is ever written.
        var settings = new EyeCtrlSettings();
        var gate = new FakeFeedbackGate();
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var log = new AdaptationLog(settings.Adaptation, gate, provider);
        Check.That(log.OnAssessment(Gated(FailureMode.SustainFailure))).IsEqualTo(AdaptationLogAction.Logged);
        provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.2); // no throw, no file side
        Check.That(log.OnAssessment(Gated())).IsEqualTo(AdaptationLogAction.Logged);
    }

    [Fact]
    public void LogCaregiverAlert_WritesKindCaregiverEntry()
    {
        string path = TempLogPath();
        try
        {
            var settings = new EyeCtrlSettings();
            var log = new AdaptationLog(settings.Adaptation, new FakeFeedbackGate(),
                new EffectiveSettingsProvider(settings.Adaptation), jsonlPath: path);

            log.LogCaregiverAlert(CaregiverAlertCondition.SustainedPoorTracking);

            var line = File.ReadAllLines(path).Single();
            Check.That(line).Contains("\"kind\":\"caregiver\"");
            Check.That(line).Contains("\"condition\":\"SustainedPoorTracking\"");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
