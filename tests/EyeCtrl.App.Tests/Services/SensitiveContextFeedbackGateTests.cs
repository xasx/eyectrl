namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using NFluent;

public class SensitiveContextFeedbackGateTests
{
    private sealed class FakeSensitiveContextDetector : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static (SensitiveContextFeedbackGate Gate, AdaptationSettings Adaptation, FakeSensitiveContextDetector Detector) Make()
    {
        var settings = new EyeCtrlSettings(); // SuppressInSensitiveContexts defaults true (1b-②)
        var detector = new FakeSensitiveContextDetector();
        var gate = new SensitiveContextFeedbackGate(settings.Adaptation, detector);
        return (gate, settings.Adaptation, detector);
    }

    [Fact]
    public void Suppressed_WhenSettingOn_AndDetectorActive()
    {
        var (gate, adaptation, detector) = Make();
        adaptation.SuppressInSensitiveContexts = true;
        detector.IsSensitiveActive = true;
        Check.That(gate.IsFeedbackSuppressed).IsTrue();
    }

    [Fact]
    public void NotSuppressed_WhenSettingOn_AndDetectorInactive()
    {
        var (gate, adaptation, detector) = Make();
        adaptation.SuppressInSensitiveContexts = true;
        detector.IsSensitiveActive = false;
        Check.That(gate.IsFeedbackSuppressed).IsFalse();
    }

    [Fact]
    public void NotSuppressed_WhenSettingOff_EvenIfDetectorActive()
    {
        // The setting is the backstop: with it off, feedback is never suppressed — even in a password field.
        var (gate, adaptation, detector) = Make();
        adaptation.SuppressInSensitiveContexts = false;
        detector.IsSensitiveActive = true;
        Check.That(gate.IsFeedbackSuppressed).IsFalse();
    }

    [Fact]
    public void NotSuppressed_WhenSettingOff_AndDetectorInactive()
    {
        var (gate, adaptation, detector) = Make();
        adaptation.SuppressInSensitiveContexts = false;
        detector.IsSensitiveActive = false;
        Check.That(gate.IsFeedbackSuppressed).IsFalse();
    }

    [Fact]
    public void ReadsLiveState_OnEachAccess()
    {
        var (gate, adaptation, detector) = Make();
        adaptation.SuppressInSensitiveContexts = true;

        detector.IsSensitiveActive = false;
        Check.That(gate.IsFeedbackSuppressed).IsFalse();

        detector.IsSensitiveActive = true; // detector flips → gate reflects immediately (no caching in the gate)
        Check.That(gate.IsFeedbackSuppressed).IsTrue();

        detector.IsSensitiveActive = false;
        Check.That(gate.IsFeedbackSuppressed).IsFalse();
    }
}
