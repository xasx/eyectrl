namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// The ground-truth sidecar: what a scripted run records about where the user was ASKED to look, and
/// when. Everything here turns on the windows being expressed in the recording's own device clock —
/// get that wrong and the analyzer scores the wrong samples against the right target.
/// </summary>
public class ScriptedGroundTruthRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eyectrl-gt-" + Guid.NewGuid().ToString("N"));

    private string Sidecar => Path.Combine(_dir, "gaze-recording.groundtruth.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static readonly PhysicalPoint Screen = new(1920, 1080);

    /// <summary>Drives one target: some pre-arrival samples, then arrived ones.</summary>
    private static void RunTarget(
        IGroundTruthSink sink, int targetId, PhysicalPoint target,
        long fromUs, int beforeArrival, int afterArrival, long stepUs = 33_333)
    {
        long t = fromUs;
        for (int i = 0; i < beforeArrival; i++, t += stepUs)
            sink.OnSample(targetId, target, t, hasArrived: false);
        for (int i = 0; i < afterArrival; i++, t += stepUs)
            sink.OnSample(targetId, target, t, hasArrived: true);
    }

    [Fact]
    public void WritesOneWindowPerTarget_WithItsKnownPosition()
    {
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);
        recorder.BeginSession("StreamEngine", Screen, settleWindowMs: 300);

        RunTarget(recorder, 0, new PhysicalPoint(384, 216), fromUs: 0, beforeArrival: 3, afterArrival: 10);
        RunTarget(recorder, 1, new PhysicalPoint(960, 540), fromUs: 1_000_000, beforeArrival: 2, afterArrival: 10);

        recorder.CompleteSession();

        var session = Analysis.GroundTruthFile.Read(Sidecar);
        Check.That(session.SchemaVersion).IsEqualTo(GroundTruthSession.CurrentSchemaVersion);
        Check.That(session.ProviderType).IsEqualTo("StreamEngine");
        Check.That(session.SettleWindowMs).IsEqualTo(300);
        Check.That(session.Windows).HasSize(2);
        Check.That(session.Windows[0].TargetId).IsEqualTo(0);
        Check.That(session.Windows[0].X).IsEqualTo(384.0);
        Check.That(session.Windows[1].Target.Y).IsEqualTo(540.0);
    }

    [Fact]
    public void ScoringWindowStartsAtArrival_NotAtFirstDisplay()
    {
        // The span before arrival is the saccade and the search. Scoring deviation over it would
        // measure the user FINDING the target rather than holding it, which is not what the
        // instrument is for — and it would inflate every p95 by the length of a saccade.
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);
        recorder.BeginSession("Mouse", Screen, 300);

        RunTarget(recorder, 0, new PhysicalPoint(100, 100), fromUs: 0, beforeArrival: 4, afterArrival: 6);
        recorder.CompleteSession();

        var window = Analysis.GroundTruthFile.Read(Sidecar).Windows[0];
        Check.That(window.DisplayedFromUs).IsEqualTo(0L);
        Check.That(window.ArrivedAtUs).IsEqualTo(4 * 33_333L);
        Check.That(window.ScoringWindow.FromUs).IsEqualTo(window.ArrivedAtUs!.Value);
        Check.That(window.SampleCount).IsEqualTo(10);   // the full displayed window is still recorded
    }

    [Fact]
    public void ArrivalIsLatched_NotOverwrittenByLaterSamples()
    {
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);
        recorder.BeginSession("Mouse", Screen, 300);

        var target = new PhysicalPoint(100, 100);
        recorder.OnSample(0, target, 1000, hasArrived: false);
        recorder.OnSample(0, target, 2000, hasArrived: true);    // arrival
        recorder.OnSample(0, target, 3000, hasArrived: true);
        recorder.CompleteSession();

        Check.That(Analysis.GroundTruthFile.Read(Sidecar).Windows[0].ArrivedAtUs).IsEqualTo(2000L);
    }

    [Fact]
    public void ATargetNeverArrivedAt_FallsBackToTheDisplayedWindow()
    {
        // A user who never found the point still produces a window — it is just the whole display
        // span, flagged by a null arrival so the analyzer can weigh or drop it knowingly.
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);
        recorder.BeginSession("Mouse", Screen, 300);

        RunTarget(recorder, 0, new PhysicalPoint(100, 100), fromUs: 500, beforeArrival: 8, afterArrival: 0);
        recorder.CompleteSession();

        var window = Analysis.GroundTruthFile.Read(Sidecar).Windows[0];
        Check.That(window.ArrivedAtUs).IsNull();
        Check.That(window.ScoringWindow.FromUs).IsEqualTo(500L);
    }

    [Fact]
    public void Contains_IsInclusiveOfBothEnds()
    {
        var window = new GroundTruthWindow(0, 10, 20, DisplayedFromUs: 0, ArrivedAtUs: 100, EndedAtUs: 200, SampleCount: 5);

        Check.That(window.Contains(100)).IsTrue();
        Check.That(window.Contains(200)).IsTrue();
        Check.That(window.Contains(99)).IsFalse();    // before arrival — the saccade
        Check.That(window.Contains(201)).IsFalse();
    }

    [Fact]
    public void AbandonedSession_WritesNothing()
    {
        // A cancelled run's windows describe targets the user may never have looked at. Keeping them
        // would hand the analyzer a ground truth that is quietly false.
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);
        recorder.BeginSession("Mouse", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(100, 100), 0, 2, 5);

        recorder.AbandonSession();

        Check.That(File.Exists(Sidecar)).IsFalse();
        Check.That(recorder.Windows).IsEmpty();
    }

    [Fact]
    public void SamplesOutsideASession_AreIgnored()
    {
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);

        recorder.OnSample(0, new PhysicalPoint(1, 1), 1000, hasArrived: true);
        recorder.CompleteSession();

        Check.That(File.Exists(Sidecar)).IsFalse();   // CompleteSession without Begin is a no-op
    }

    [Fact]
    public void ARestartedSessionDropsThePartialWindowOfTheOneItInterrupted()
    {
        // Pins the discard-before-clear order, which ASecondSession cannot: there, the first session
        // was already closed by CompleteSession, so no open window existed to resurrect. Here the
        // first session is interrupted MID-TARGET — the path a double calibration request takes, since
        // it re-enters StartSession and so BeginSession while a target's window is still open.
        //
        // Under the old order (clear, then close) the interrupted target was appended AFTER the clear,
        // so the second session's sidecar carried a window from the first.
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);

        recorder.BeginSession("Mouse", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(111, 111), fromUs: 0, beforeArrival: 1, afterArrival: 5);
        // ...no CompleteSession: the run is interrupted here.

        recorder.BeginSession("Mouse", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(222, 222), fromUs: 5_000_000, beforeArrival: 1, afterArrival: 5);
        recorder.CompleteSession();

        var windows = Analysis.GroundTruthFile.Read(Sidecar).Windows;
        Check.That(windows).HasSize(1);
        Check.That(windows[0].X).IsEqualTo(222.0);                  // only the second session's target
        Check.That(windows.Any(w => w.X == 111.0)).IsFalse();       // the interrupted one is gone
    }

    [Fact]
    public void AnAbandonedSessionLeavesNoPartialWindowBehindForTheNextOne()
    {
        // The same ordering hazard on the abandon path: cancel mid-target, then run a fresh session.
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);

        recorder.BeginSession("Mouse", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(111, 111), fromUs: 0, beforeArrival: 1, afterArrival: 5);
        recorder.AbandonSession();

        recorder.BeginSession("Mouse", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(222, 222), fromUs: 5_000_000, beforeArrival: 1, afterArrival: 5);
        recorder.CompleteSession();

        var windows = Analysis.GroundTruthFile.Read(Sidecar).Windows;
        Check.That(windows).HasSize(1);
        Check.That(windows[0].X).IsEqualTo(222.0);
    }

    [Fact]
    public void ASecondSession_ReplacesTheFirst()
    {
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);

        recorder.BeginSession("Mouse", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(1, 1), 0, 1, 5);
        recorder.CompleteSession();

        recorder.BeginSession("StreamEngine", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(2, 2), 9_000_000, 1, 5);
        recorder.CompleteSession();

        var session = Analysis.GroundTruthFile.Read(Sidecar);
        Check.That(session.Windows).HasSize(1);
        Check.That(session.ProviderType).IsEqualTo("StreamEngine");
        Check.That(session.Windows[0].X).IsEqualTo(2.0);
    }

    [Fact]
    public void Windows_IsASnapshot_NotTheLiveList()
    {
        // The list is appended to from the GAZE thread at each target transition while whoever ends the
        // session reads it. Handing out the live reference lets a caller enumerate a List<T> mid-resize:
        // no exception guaranteed, just a torn read or a silently missing target — and a ground truth
        // missing a target scores the wrong samples against the remaining ones.
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);
        recorder.BeginSession("Mouse", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(100, 100), 0, 1, 5);
        RunTarget(recorder, 1, new PhysicalPoint(200, 200), 1_000_000, 1, 5);

        var before = recorder.Windows;
        RunTarget(recorder, 2, new PhysicalPoint(300, 300), 2_000_000, 1, 5);

        // The earlier snapshot did not grow, and a fresh read sees the new window.
        Check.That(before).HasSize(1);           // targets 0 and 1 closed; 1 is still open
        Check.That(recorder.Windows).HasSize(2);
        Check.That(recorder.Windows).Not.IsSameReferenceAs(recorder.Windows);
    }

    [Fact]
    public void CompleteSession_ReportsAWriteFault_RatherThanThrowingIntoTheCalibration()
    {
        // Covers the whole method, snapshot included: CompleteSession is called from the calibration
        // completion path, so ANY throw out of it turns a lost diagnostic into a failed calibration —
        // which for this user means redoing the one thing that makes their cursor land where they look.
        var recorder = new ScriptedGroundTruthRecorder(Path.Combine(_dir, "nested\0bad", "gt.json"));
        recorder.BeginSession("Mouse", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(1, 1), 0, 1, 5);

        Check.ThatCode(() => recorder.CompleteSession()).DoesNotThrow();
        Check.That(recorder.IsRecording).IsFalse();
    }

    [Fact]
    public void WriteFault_IsReportedAndDoesNotThrow()
    {
        // The sidecar rides along with a calibration that has already been persisted; losing it must
        // never cost the calibration.
        var faults = new List<Exception>();
        var recorder = new ScriptedGroundTruthRecorder(
            Path.Combine(_dir, "nested\0bad", "gt.json"), faults.Add);

        recorder.BeginSession("Mouse", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(1, 1), 0, 1, 5);

        Check.ThatCode(() => recorder.CompleteSession()).DoesNotThrow();
        Check.That(faults).HasSize(1);
    }

    [Fact]
    public void ReaderRefusesAnUnknownSchema()
    {
        // Silently reading an unfamiliar shape would produce a ground truth that is not one, and every
        // absolute-accuracy number derived from it would be wrong without looking wrong.
        string json = """
        {"schemaVersion":999,"providerType":"Mouse","screenWidthPhysical":1920,
         "screenHeightPhysical":1080,"settleWindowMs":300,"windows":[]}
        """;

        Check.ThatCode(() => Analysis.GroundTruthFile.Parse(json))
             .Throws<InvalidOperationException>();
    }
}
