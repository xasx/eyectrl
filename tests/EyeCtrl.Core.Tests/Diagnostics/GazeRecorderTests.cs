namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// The file side of the recorder: what lands on disk, that it is bounded, and that shutdown is
/// budgeted. Timing-sensitive assertions wait on the observable result (the file) rather than
/// sleeping for a fixed interval, so they do not flake under load.
/// </summary>
public class GazeRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eyectrl-rec-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "gaze-recording.csv");

    private static GazeRecordingHeader Header() => new(
        ProviderType: "StreamEngine",
        ScreenWidthPhysical: 3840,
        ScreenHeightPhysical: 2400,
        CalibrationActive: true,
        CalibrationVersion: "2026-09-11T10:00:00.0000000Z",
        SettingsVersion: 7);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Waits for the background writer to produce a file satisfying <paramref name="until"/>.
    /// Reads defensively: the writer appends and may rotate concurrently, so a read can legitimately
    /// collide with it — that is the harness racing the subject, not a failure of the subject.
    /// </summary>
    private string WaitForFile(Func<string, bool> until, int budgetMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(budgetMs);
        while (DateTime.UtcNow < deadline)
        {
            string text = TryRead();
            if (text.Length > 0 && until(text)) return text;
            Thread.Sleep(15);
        }
        return TryRead();
    }

    private static bool Wait(Func<bool> until, int budgetMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(budgetMs);
        while (DateTime.UtcNow < deadline)
        {
            if (until()) return true;
            Thread.Sleep(15);
        }
        return until();
    }

    private string TryRead()
    {
        try
        {
            using var stream = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return string.Empty; }   // covers file-not-found and directory-not-found too
    }

    [Fact]
    public void WritesJsonHeaderThenCsvSamples()
    {
        using (var recorder = new GazeRecorder(Path_, Header()))
        {
            recorder.Record(new GazePoint(100.5, 200.25, 1_000, IsValid: true));
            recorder.Record(new GazePoint(0, 0, 2_000, IsValid: false));
            recorder.RequestFlush();

            WaitForFile(t => t.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length >= 3);
        }

        var lines = File.ReadAllLines(Path_).Where(l => l.Length > 0).ToArray();
        Check.That(lines[0]).StartsWith("# {");
        Check.That(lines[0]).Contains("\"providerType\":\"StreamEngine\"");
        Check.That(lines[0]).Contains("\"calibrationActive\":true");
        Check.That(lines[1]).IsEqualTo("100.5,200.25,1000,1");
        Check.That(lines[2]).IsEqualTo("0,0,2000,0");
    }

    [Fact]
    public void HeaderIsWrittenOnce()
    {
        using (var recorder = new GazeRecorder(Path_, Header()))
        {
            recorder.Record(new GazePoint(1, 1, 1000, true));
            recorder.RequestFlush();
            WaitForFile(t => t.Contains("1,1,1000,1"));

            recorder.Record(new GazePoint(2, 2, 2000, true));
            recorder.RequestFlush();
            WaitForFile(t => t.Contains("2,2,2000,1"));
        }

        int headers = File.ReadAllLines(Path_).Count(l => l.StartsWith("# "));
        Check.That(headers).IsEqualTo(1);
    }

    [Fact]
    public void RecordIsInvariantCulture()
    {
        // A comma decimal separator would silently corrupt every coordinate in the file on a German
        // machine — which is this one.
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            using (var recorder = new GazeRecorder(Path_, Header()))
            {
                recorder.Record(new GazePoint(1234.5, 987.25, 5_000, true));
                recorder.RequestFlush();
                WaitForFile(t => t.Contains("1234.5"));
            }

            Check.That(File.ReadAllText(Path_)).Contains("1234.5,987.25,5000,1");
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public void RotatesWhenTheCapIsExceeded()
    {
        // Pre-seed a file already past the cap: the next append must rotate rather than grow without
        // bound, and the new file must carry its own header so it is self-describing.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, new string('x', (int)GazeRecorder.MaxRecordingBytes + 1));

        var faults = new List<Exception>();
        using (var recorder = new GazeRecorder(Path_, Header(), onFault: faults.Add))
        {
            recorder.Record(new GazePoint(7, 8, 9_000, true));
            recorder.RequestFlush();
            // Wait on the rotation itself rather than by re-reading a 16 MB file, which would race the
            // writer's File.Move and report a harness collision as a rotation failure.
            Wait(() => File.Exists(Path_ + ".1"));
        }

        Check.That(faults).IsEmpty();
        Check.That(File.Exists(Path_ + ".1")).IsTrue();
        var lines = File.ReadAllLines(Path_).Where(l => l.Length > 0).ToArray();
        Check.That(lines[0]).StartsWith("# {");
        Check.That(lines).Contains("7,8,9000,1");
        Check.That(new FileInfo(Path_).Length).IsStrictlyLessThan(GazeRecorder.MaxRecordingBytes);
    }

    [Fact]
    public void OverflowDropsOldestAndCountsThem_RatherThanBlocking()
    {
        // The gaze thread must never wait on the writer. With a tiny ring and no drain, the producer
        // laps the consumer; the contract is that Record still returns immediately and the loss is
        // counted so the analyzer can reject the recording.
        using var recorder = new GazeRecorder(Path_, Header(), capacity: 8);

        for (int i = 0; i < 200; i++)
            recorder.Record(new GazePoint(i, i, i * 1000, true));

        Check.That(recorder.RecordedSamples).IsEqualTo(200L);
        Check.That(recorder.DroppedSamples).IsStrictlyGreaterThan(0L);
    }

    [Fact]
    public void RequestFlush_DoesNotBlockTheCaller()
    {
        // Stop() calls this, and Stop() must not spend the user's shutdown on a diagnostic.
        using var recorder = new GazeRecorder(Path_, Header());
        recorder.Record(new GazePoint(1, 1, 1000, true));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        recorder.RequestFlush();
        sw.Stop();

        Check.That(sw.ElapsedMilliseconds).IsStrictlyLessThan(100L);
    }

    [Fact]
    public void Dispose_DrainsWhatIsBuffered()
    {
        using (var recorder = new GazeRecorder(Path_, Header()))
        {
            for (int i = 0; i < 10; i++)
                recorder.Record(new GazePoint(i, i, i * 1000, true));
        }   // Dispose signals a final drain and joins on a budget

        var text = File.ReadAllText(Path_);
        Check.That(text).Contains("9,9,9000,1");
    }

    [Fact]
    public void Dispose_WritesATrailerCarryingTheDropCount()
    {
        // The whole safety net in one assertion: the drop count has to survive onto DISK, because the
        // machine that analyzes a recording is generally not the machine that made it, and a count that
        // lives only in a process that has since exited protects nobody.
        using (var recorder = new GazeRecorder(Path_, Header(), capacity: 8))
        {
            for (int i = 0; i < 200; i++)
                recorder.Record(new GazePoint(i, i, i * 1000, true));
        }

        var lines = File.ReadAllLines(Path_);
        var trailer = lines.Last(l => l.StartsWith(GazeRecorder.TrailerPrefix, StringComparison.Ordinal));

        Check.That(trailer).Contains("\"droppedSamples\":");
        Check.That(trailer).Contains("\"recordedSamples\":200");
        // Not zero: a tiny ring with no drain time is the overflow case, and a trailer that reported
        // "no drops" here would be worse than no trailer at all.
        Check.That(trailer).Not.Contains("\"droppedSamples\":0");
    }

    [Fact]
    public void Trailer_IsTheLastLine_AfterTheFinalDrain()
    {
        // Ordering is the contract: a trailer written before the final drain would sit in the middle of
        // the samples, and a reader taking "last trailer wins" would be describing only part of the file.
        using (var recorder = new GazeRecorder(Path_, Header()))
        {
            for (int i = 0; i < 10; i++)
                recorder.Record(new GazePoint(i, i, i * 1000, true));
        }

        var lines = File.ReadAllLines(Path_).Where(l => l.Length > 0).ToArray();

        Check.That(lines[^1]).StartsWith(GazeRecorder.TrailerPrefix);
        Check.That(lines[^2]).IsEqualTo("9,9,9000,1");
        Check.That(lines[^1]).Contains("\"droppedSamples\":0");
    }

    [Fact]
    public void Dispose_ReportsTheCounts_EvenWhenNothingWasDropped()
    {
        // The clean case still reports. An operator who only ever hears about failures cannot tell
        // "no drops" from "the report is broken", and this line is their evidence the session is usable.
        GazeRecordingTrailer? closed = null;
        using (var recorder = new GazeRecorder(Path_, Header(), onClosed: t => closed = t))
        {
            for (int i = 0; i < 5; i++)
                recorder.Record(new GazePoint(i, i, i * 1000, true));
        }

        Check.That(closed).IsNotNull();
        Check.That(closed!.Value.RecordedSamples).IsEqualTo(5L);
        Check.That(closed.Value.DroppedSamples).IsEqualTo(0L);
    }

    [Fact]
    public void SessionThatRecordedNothing_LeavesAHeaderAndATrailerSayingSo()
    {
        // A recorder that ran with the tracker disconnected writes its header on the first drain (that
        // predates the trailer) and now closes it properly. The pair is worth having: "recording was on,
        // the device produced nothing" and "recording was never on" are different operator mistakes, and
        // only the first one leaves a file. An empty-but-closed recording is also USABLE by the
        // analyzer's rules — zero samples with zero dropped is an honest zero, not a hole.
        using (var recorder = new GazeRecorder(Path_, Header()))
        {
            // no samples
        }

        var lines = File.ReadAllLines(Path_).Where(l => l.Length > 0).ToArray();

        Check.That(lines).HasSize(2);
        Check.That(lines[0]).StartsWith("# ");
        Check.That(lines[1]).StartsWith(GazeRecorder.TrailerPrefix);
        Check.That(lines[1]).Contains("\"recordedSamples\":0");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var recorder = new GazeRecorder(Path_, Header());
        recorder.Dispose();
        Check.ThatCode(() => recorder.Dispose()).DoesNotThrow();
    }

    [Fact]
    public void Dispose_AfterAFaultingDrain_LetsNothingEscapeTheWriterThread()
    {
        // Shutdown must not be able to kill the process. The writer runs on a background thread, where
        // ANY unhandled exception terminates the app — so a diagnostic that throws during teardown
        // costs the user their session. Here the recording file is held exclusively so every drain
        // throws; the recorder must self-disable, and Dispose must then complete silently.
        //
        // NB this covers the faulting drain, not the >2 s STALLED drain that motivated the guard (a
        // stall needs a genuinely slow filesystem and is not deterministically reproducible here). That
        // path is closed structurally instead, by three independent mechanisms: the stop flag is
        // re-read after the drain, the wait is guarded against ObjectDisposedException, and Dispose
        // only disposes the wake event when the join actually succeeded.
        Directory.CreateDirectory(_dir);
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        Exception? unhandled = null;
        void OnUnhandled(object? _, UnhandledExceptionEventArgs e) => unhandled = e.ExceptionObject as Exception;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        try
        {
            using (var exclusive = new FileStream(Path_, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var recorder = new GazeRecorder(Path_, Header(), onFault: faults.Enqueue);
                recorder.Record(new GazePoint(1, 1, 1000, true));
                recorder.RequestFlush();

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (recorder.IsRecording && DateTime.UtcNow < deadline) Thread.Sleep(15);

                Check.ThatCode(() => recorder.Dispose()).DoesNotThrow();
            }

            Thread.Sleep(50);   // give any surviving writer a chance to touch the disposed event
            Check.That(unhandled).IsNull();
        }
        finally { AppDomain.CurrentDomain.UnhandledException -= OnUnhandled; }
    }

    [Fact]
    public void Dispose_AfterTheWriterSelfDisabled_StillCompletes()
    {
        // The writer returns early on a fault, so the join finds an already-finished thread. Dispose
        // must neither hang nor throw on that path.
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        string unwritable = Path.Combine(_dir, "nested\0bad", "gaze.csv");
        var recorder = new GazeRecorder(unwritable, Header(), onFault: faults.Enqueue);

        recorder.Record(new GazePoint(1, 1, 1000, true));
        recorder.RequestFlush();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (recorder.IsRecording && DateTime.UtcNow < deadline) Thread.Sleep(15);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Check.ThatCode(() => recorder.Dispose()).DoesNotThrow();
        sw.Stop();

        Check.That(sw.ElapsedMilliseconds).IsStrictlyLessThan(2000L);   // no full-budget wait
    }

    [Fact]
    public void RecordAfterDispose_IsANoOp()
    {
        var recorder = new GazeRecorder(Path_, Header());
        recorder.Dispose();

        Check.ThatCode(() => recorder.Record(new GazePoint(1, 1, 1000, true))).DoesNotThrow();
        Check.That(recorder.IsRecording).IsFalse();
    }

    [Fact]
    public void WriterFault_SelfDisables_WithoutThrowingToTheProducer()
    {
        // An unwritable path stands in for a full disk or a revoked permission. The recorder must give
        // up on the recording, report once, and leave the gaze thread untouched.
        //
        // The fault is captured in a concurrent collection: it is added on the WRITER thread, and the
        // volatile IsRecording flag this test polls is written BEFORE the add, so polling it
        // establishes no happens-before edge for the list. A plain List would be a data race that
        // happens to pass.
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        string unwritable = Path.Combine(_dir, "nested\0bad", "gaze.csv");

        var recorder = new GazeRecorder(unwritable, Header(), onFault: faults.Enqueue);
        recorder.Record(new GazePoint(1, 1, 1000, true));
        recorder.RequestFlush();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (recorder.IsRecording && DateTime.UtcNow < deadline) Thread.Sleep(15);

        Check.That(recorder.IsRecording).IsFalse();
        Check.ThatCode(() => recorder.Record(new GazePoint(2, 2, 2000, true))).DoesNotThrow();

        // Dispose joins the writer thread, which IS a full fence — only after it is the fault list
        // safely observable.
        recorder.Dispose();
        Check.That(faults).HasSize(1);
    }
}
