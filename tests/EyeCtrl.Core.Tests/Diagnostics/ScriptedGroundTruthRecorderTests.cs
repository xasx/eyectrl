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
            sink.OnSample(targetId, target, new GazePoint(target.X, target.Y, t, IsValid: true), hasArrived: false);
        for (int i = 0; i < afterArrival; i++, t += stepUs)
            sink.OnSample(targetId, target, new GazePoint(target.X, target.Y, t, IsValid: true), hasArrived: true);
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
        recorder.OnSample(0, target, new GazePoint(100, 100, 1000, IsValid: true), hasArrived: false);
        recorder.OnSample(0, target, new GazePoint(100, 100, 2000, IsValid: true), hasArrived: true);    // arrival
        recorder.OnSample(0, target, new GazePoint(100, 100, 3000, IsValid: true), hasArrived: true);
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

        recorder.OnSample(0, new PhysicalPoint(1, 1), new GazePoint(1, 1, 1000, IsValid: true), hasArrived: true);
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

    [Fact]
    public void WindowsCarryTheirOwnRawSamples_SoTheSidecarIsSelfContained()
    {
        // The measure-only probe must not depend on the separate recorder file: that one path is
        // appended to and rotated across sessions, so a later launch could invalidate a measurement —
        // and requiring it would hide the whole probe behind a global diagnostics flag. The samples
        // are stored raw, the invalid one included: it carries no position, but it does carry the
        // window's timeline and true sample count.
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);
        recorder.BeginSession("StreamEngine", Screen, 300);

        var target = new PhysicalPoint(100, 100);
        recorder.OnSample(0, target, new GazePoint(101.5, 99.25, 1_000, IsValid: true), hasArrived: false);
        recorder.OnSample(0, target, new GazePoint(0, 0, 2_000, IsValid: false), hasArrived: false);
        recorder.OnSample(0, target, new GazePoint(102.5, 98.75, 3_000, IsValid: true), hasArrived: true);
        recorder.CompleteSession();

        var window = Analysis.GroundTruthFile.Read(Sidecar).Windows.Single();
        Check.That(window.Samples).HasSize(3);
        Check.That(window.Samples[0].X).IsEqualTo(101.5);
        Check.That(window.Samples[0].TimestampUs).IsEqualTo(1_000L);
        Check.That(window.Samples[1].Valid).IsFalse();
        Check.That(window.Samples[2].Valid).IsTrue();
        Check.That(window.SampleCount).IsEqualTo(window.Samples.Count);
    }

    [Theory]
    [InlineData(GroundTruthOutcome.Completed)]
    [InlineData(GroundTruthOutcome.Partial)]
    [InlineData(GroundTruthOutcome.Abandoned)]
    [InlineData(GroundTruthOutcome.Failed)]
    public void Outcome_IsWrittenByItsName(GroundTruthOutcome outcome)
    {
        // The measure-only probe keeps the sidecar on every ending, so the outcome is the only thing
        // that later tells a full five-target run from one the user abandoned or the tracker cut
        // short. Written by name because the file is also read by an operator.
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);
        recorder.BeginSession("StreamEngine", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(100, 100), 0, 1, 3);
        recorder.CompleteSession(outcome);

        Check.That(Analysis.GroundTruthFile.Read(Sidecar).Outcome).IsEqualTo(outcome);
    }

    [Fact]
    public void ASchema1SidecarWithoutOutcomeOrSamples_ParsesWithTheOldMeaning()
    {
        // Schema stays 1: both additions are optional JSON members, so a sidecar written before them
        // is still a well-formed session. A missing outcome means Completed — before the probe, a
        // sidecar only ever existed for a run that finished.
        string json = """
        {"schemaVersion":1,"providerType":"Mouse","screenWidthPhysical":1920,
         "screenHeightPhysical":1080,"settleWindowMs":300,"windows":[
           {"targetId":0,"x":10,"y":20,"displayedFromUs":0,"arrivedAtUs":100,
            "endedAtUs":200,"sampleCount":5}]}
        """;

        var session = Analysis.GroundTruthFile.Parse(json);

        Check.That(session.Outcome).IsEqualTo(GroundTruthOutcome.Completed);
        Check.That(session.Windows.Single().Samples).IsEmpty();
    }

    [Fact]
    public void Outcome_IsReadCaseInsensitively_SoAHandEditedSidecarStillParses()
    {
        // The file is a text artifact an operator may inspect or repair; a reader that demanded the
        // exact casing the serializer happened to choose would be a trap for exactly the reader it
        // is for. Asserts the converter's case-insensitive read rather than trusting it.
        string json = """
        {"schemaVersion":1,"providerType":"Mouse","screenWidthPhysical":1920,
         "screenHeightPhysical":1080,"settleWindowMs":300,"outcome":"PARTIAL","windows":[]}
        """;

        Check.That(Analysis.GroundTruthFile.Parse(json).Outcome).IsEqualTo(GroundTruthOutcome.Partial);
    }

    // ── One file per probe; a mode on every sidecar ──────────────────────────

    [Fact]
    public void AProbeSession_WritesItsOwnTimestampedFile_NeverTheConfiguredOne()
    {
        // The plan is probe → Tobii recalibration → probe again; both probes must survive to be
        // compared, and a normal calibration's sidecar must not be overwritten either. The probe gets
        // its own timestamped sibling and never touches the configured path.
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);
        recorder.BeginSession("StreamEngine", Screen, 300, GroundTruthMode.MeasureOnly);
        RunTarget(recorder, 0, new PhysicalPoint(100, 100), 0, 1, 5);
        recorder.CompleteSession(GroundTruthOutcome.Partial);

        Check.That(File.Exists(Sidecar)).IsFalse();
        var files = Directory.GetFiles(_dir, "bias-probe-*.groundtruth.json");
        Check.That(files).HasSize(1);

        var session = Analysis.GroundTruthFile.Read(files[0]);
        Check.That(session.Mode).IsEqualTo(GroundTruthMode.MeasureOnly);
        Check.That(session.Outcome).IsEqualTo(GroundTruthOutcome.Partial);
        Check.That(session.Windows.Single().Samples).Not.IsEmpty();
    }

    [Fact]
    public void TwoProbeSessions_ProduceTwoReadableFiles_AndTheFirstIsNotOverwritten()
    {
        // The second probe must not erase the first — that is the whole reason for a per-session name.
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);
        recorder.BeginSession("StreamEngine", Screen, 300, GroundTruthMode.MeasureOnly);
        RunTarget(recorder, 0, new PhysicalPoint(100, 100), 0, 1, 5);
        recorder.CompleteSession(GroundTruthOutcome.Completed);

        recorder.BeginSession("StreamEngine", Screen, 300, GroundTruthMode.MeasureOnly);
        RunTarget(recorder, 0, new PhysicalPoint(200, 200), 5_000_000, 1, 5);
        recorder.CompleteSession(GroundTruthOutcome.Abandoned);

        var files = Directory.GetFiles(_dir, "bias-probe-*.groundtruth.json");
        Check.That(files).HasSize(2);

        var sessions = files.Select(Analysis.GroundTruthFile.Read).ToList();
        Check.That(sessions.Select(s => s.Outcome)).Contains(new[] { GroundTruthOutcome.Completed, GroundTruthOutcome.Abandoned });
        Check.That(sessions.Select(s => s.Mode)).ContainsOnlyElementsThatMatch(m => m == GroundTruthMode.MeasureOnly);
        Check.That(sessions.Select(s => s.Windows.Single().X)).Contains(new[] { 100.0, 200.0 });
    }

    [Fact]
    public void TwoProbesInTheSameMillisecond_GetDistinctNames()
    {
        // The collision path, which the UI cannot reach (a probe takes seconds) but a clock can:
        // without the -n suffix the second probe would silently replace the first.
        var clock = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        var recorder = new ScriptedGroundTruthRecorder(Sidecar, utcNow: () => clock);

        recorder.BeginSession("StreamEngine", Screen, 300, GroundTruthMode.MeasureOnly);
        RunTarget(recorder, 0, new PhysicalPoint(100, 100), 0, 1, 5);
        recorder.CompleteSession();

        recorder.BeginSession("StreamEngine", Screen, 300, GroundTruthMode.MeasureOnly);
        RunTarget(recorder, 0, new PhysicalPoint(200, 200), 0, 1, 5);
        recorder.CompleteSession();

        var files = Directory.GetFiles(_dir, "bias-probe-*.groundtruth.json");
        Check.That(files).HasSize(2);
        Check.That(files.Any(f => f.EndsWith("-2.groundtruth.json", StringComparison.OrdinalIgnoreCase))).IsTrue();
        Check.That(files.Select(Analysis.GroundTruthFile.Read).Select(s => s.Windows.Single().X))
            .Contains(new[] { 100.0, 200.0 });
    }

    [Fact]
    public void ACloseFault_KeepsTheWindowsAlreadyClosed_AndReportsOnce()
    {
        // The real trigger is an in-flight gaze callback racing the open-window snapshot; the seam
        // makes it deterministic. A faulted close must cost only the open window — the windows already
        // closed are still written, and the fault is reported once. Merging the close's catch back
        // into the write's try loses every closed window and turns this red.
        var faults = new List<Exception>();
        var recorder = new ScriptedGroundTruthRecorder(Sidecar, faults.Add);
        recorder.BeginSession("StreamEngine", Screen, 300);

        RunTarget(recorder, 0, new PhysicalPoint(100, 100), 0, 1, 5);            // window 0 open
        RunTarget(recorder, 1, new PhysicalPoint(200, 200), 1_000_000, 1, 5);    // closes window 0

        recorder.SnapshotOpenSamples = _ => throw new InvalidOperationException("torn snapshot");
        Check.ThatCode(() => recorder.CompleteSession()).DoesNotThrow();

        Check.That(faults).HasSize(1);
        Check.That(File.Exists(Sidecar)).IsTrue();
        var session = Analysis.GroundTruthFile.Read(Sidecar);
        Check.That(session.Windows).HasSize(1);
        Check.That(session.Windows[0].X).IsEqualTo(100.0);
    }

    [Fact]
    public void ANormalSession_KeepsTheConfiguredPath_AndRecordsCalibrationMode()
    {
        var recorder = new ScriptedGroundTruthRecorder(Sidecar);
        recorder.BeginSession("StreamEngine", Screen, 300);
        RunTarget(recorder, 0, new PhysicalPoint(100, 100), 0, 1, 5);
        recorder.CompleteSession();

        Check.That(File.Exists(Sidecar)).IsTrue();
        var session = Analysis.GroundTruthFile.Read(Sidecar);
        Check.That(session.Mode).IsEqualTo(GroundTruthMode.Calibration);
        // The mode must be WRITTEN, not merely defaulted on read: the file is inspected by an operator.
        var raw = File.ReadAllText(Sidecar);
        Check.That(raw).Contains("\"mode\"");
        Check.That(raw).Contains("calibration");
        Check.That(Directory.GetFiles(_dir, "bias-probe-*.groundtruth.json")).IsEmpty();
    }
}
