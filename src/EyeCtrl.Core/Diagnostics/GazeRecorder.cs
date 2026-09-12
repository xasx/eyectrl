namespace EyeCtrl.Core.Diagnostics;

using System.Globalization;
using System.Text;
using System.Text.Json;
using EyeCtrl.Core.Models;

/// <summary>
/// The shipped <see cref="IGazeRecordingSink"/>: a bounded ring buffer written by the gaze thread and
/// drained to a local file by a background thread.
/// <para>
/// The split exists because the gaze thread is the one thread in this application that must never
/// block. <see cref="Record"/> therefore does no I/O, takes no lock, allocates nothing and never
/// waits — it stores the sample (a value type) into a pre-sized array and returns. Everything
/// expensive — formatting, the file, rotation — happens on the writer thread.
/// </para>
/// <para>
/// <b>Overflow drops, it never blocks.</b> If the writer cannot keep up, the oldest unread samples
/// are overwritten and <see cref="DroppedSamples"/> counts them. A recording with a gap in it is a
/// flawed measurement; a gaze thread stalled behind a disk write is a user who cannot move their
/// cursor. The count is written to the file as a closing trailer, and reported through the
/// <c>onClosed</c> callback, so a lossy recording is refused by the analyzer instead of quietly
/// measured — the hole would otherwise read as a genuine tracker dropout, which is exactly the
/// quantity these recordings exist to measure.
/// </para>
/// <para>
/// <b>Format</b>: one <c>#</c>-prefixed JSON header line, then CSV <c>x,y,timestampUs,isValid</c> —
/// one line per raw sample, invariant culture — then, on a clean close, one <c>#!</c>-prefixed JSON
/// trailer line. CSV because a recording is a long column of numbers (JSON per sample roughly doubles
/// the file for no gain), and JSON for the two side-channel lines because they are structured and must
/// stay readable as fields are added. The distinct <c>#!</c> marker is what lets a reader tell a
/// trailer from the header a rotation repeats.
/// </para>
/// </summary>
public sealed class GazeRecorder : IGazeRecordingSink
{
    /// <summary>
    /// Size cap before the file rotates to <c>.1</c> (single generation, replaced) — the same
    /// mechanism as <c>AdaptationLog</c>, with a cap sized for this file's job rather than that one's.
    /// At roughly 40 bytes per CSV line this holds about 400k samples: over an hour at 90 Hz, so a
    /// measurement session is never cut in half and then stitched back together by the analyzer.
    /// </summary>
    public const long MaxRecordingBytes = 16L * 1024 * 1024;

    /// <summary>How long <see cref="Dispose"/> waits for the writer to drain before abandoning it.</summary>
    private const int ShutdownBudgetMs = 2000;

    /// <summary>Writer wake-up interval. The producer never signals, so this is what bounds staleness.</summary>
    private const int DrainIntervalMs = 250;

    /// <summary>Marks the closing trailer line, distinguishing it from the <c>#</c> header a rotation repeats.</summary>
    public const string TrailerPrefix = "#!";

    private static readonly JsonSerializerOptions HeaderJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _filePath;
    private readonly GazePoint[] _buffer;
    private readonly int _mask;
    private readonly Action<Exception>? _onFault;
    private readonly Action<GazeRecordingTrailer>? _onClosed;
    private readonly GazeRecordingHeader _header;

    // Single producer (gaze thread) writes _written; single consumer (writer thread) writes _read.
    // Both are monotonic and read across threads, hence volatile-by-Interlocked rather than plain.
    private long _written;
    private long _read;
    private long _dropped;

    private readonly Thread _writerThread;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly StringBuilder _pending = new(capacity: 16 * 1024);
    private volatile bool _stopping;
    private volatile bool _faulted;
    private bool _headerWritten;
    // Volatile like its two siblings above: written by the disposing thread, read per sample by the
    // gaze thread in Record, so a cached read there would keep the ring live past teardown.
    private volatile bool _disposed;

    /// <summary>Initializes the recorder and starts its writer thread.</summary>
    /// <param name="filePath">Full path of the recording file. Its directory is created if absent.</param>
    /// <param name="header">Session side-channel, written once as the first line.</param>
    /// <param name="capacity">Ring capacity in samples; rounded up to a power of two. Default 8192 (~90 s at 90 Hz).</param>
    /// <param name="onFault">Optional one-shot fault report, invoked at most once when the recorder self-disables.</param>
    /// <param name="onClosed">
    /// Optional close report carrying the sample and drop counts, invoked once from <see cref="Dispose"/>.
    /// The trailer on disk is what the analyzer reads; this is so the count also reaches the log, where
    /// the operator sees it at the end of the session rather than after carrying a ruined recording to a
    /// different machine.
    /// </param>
    public GazeRecorder(
        string filePath,
        GazeRecordingHeader header,
        int capacity = 8192,
        Action<Exception>? onFault = null,
        Action<GazeRecordingTrailer>? onClosed = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A recording path is required.", nameof(filePath));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");

        _filePath = filePath;
        _header = header;
        _onFault = onFault;
        _onClosed = onClosed;

        int size = 1;
        while (size < capacity) size <<= 1;   // power of two so the index wrap is a mask, not a modulo
        _buffer = new GazePoint[size];
        _mask = size - 1;

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,      // never keeps the process alive; a diagnostic must not delay exit
            Name = "EyeCtrl.GazeRecorder",
            Priority = ThreadPriority.BelowNormal,
        };
        _writerThread.Start();
    }

    /// <inheritdoc />
    public bool IsRecording => !_faulted && !_disposed;

    /// <summary>Samples overwritten before the writer could persist them. Non-zero invalidates a recording.</summary>
    public long DroppedSamples => Interlocked.Read(ref _dropped);

    /// <summary>Samples handed to the writer so far — the producer's count, including any later dropped.</summary>
    public long RecordedSamples => Interlocked.Read(ref _written);

    /// <inheritdoc />
    /// <remarks>
    /// Gaze thread. No I/O, no lock, no allocation, no wait — a store into the ring and one increment.
    /// </remarks>
    public void Record(in GazePoint sample)
    {
        if (_faulted || _disposed) return;

        long written = _written;
        _buffer[written & _mask] = sample;

        // Publish the slot only after the value is in it: the writer reads _written first, so a reader
        // that sees the new count is guaranteed to see the sample.
        Interlocked.Exchange(ref _written, written + 1);

        // Overflow: the producer has lapped the consumer. Advance the read cursor past what was just
        // overwritten and count it, rather than stalling the gaze thread.
        long read = Interlocked.Read(ref _read);
        long overflow = (written + 1) - read - _buffer.Length;
        if (overflow > 0)
        {
            Interlocked.Add(ref _read, overflow);
            Interlocked.Add(ref _dropped, overflow);
        }
    }

    /// <inheritdoc />
    public void RequestFlush() => _wake.Set();

    private void WriterLoop()
    {
        while (true)
        {
            try
            {
                DrainOnce();
            }
            catch (Exception ex)
            {
                // A recording is worth exactly nothing next to the gaze pipeline it observes, so any
                // writer fault ends the recording for this session instead of retrying forever against
                // a full disk or a revoked permission.
                SelfDisable(ex);
                return;
            }

            // Read AFTER the drain, never before it. A drain that overran Dispose's join budget — a
            // file write stalled on a dying disk or a network profile, which is exactly when a disk
            // diagnostic is most likely to be mid-write — would otherwise fall through on a stale
            // `false` and touch an event Dispose had already disposed. An unhandled
            // ObjectDisposedException on this background thread terminates the process, so a
            // diagnostic would be killing the app during shutdown.
            if (_stopping)
            {
                // One last pass before leaving: Dispose may have set the flag WHILE the drain above was
                // running, in which case everything recorded after that drain began is still in the
                // ring. Returning straight out would silently truncate the recording — which is how
                // this shape was caught, by a round-trip test losing 740 of 1000 samples under load.
                try
                {
                    DrainOnce();
                    WriteTrailer();
                }
                catch (Exception ex) { SelfDisable(ex); }
                return;
            }

            try
            {
                _wake.Wait(DrainIntervalMs);
                _wake.Reset();
            }
            catch (ObjectDisposedException)
            {
                // Belt to the brace above: Dispose won the race between the flag read and the wait.
                // There is nothing left to drain into, and this thread must not throw.
                return;
            }
        }
    }

    private void DrainOnce()
    {
        long read = Interlocked.Read(ref _read);
        long written = Interlocked.Read(ref _written);
        if (read >= written && _headerWritten) return;

        _pending.Clear();

        if (!_headerWritten)
        {
            _pending.Append("# ")
                    .Append(JsonSerializer.Serialize(_header, HeaderJsonOptions))
                    .Append('\n');
        }

        // Re-read the ring from the read cursor. A sample the producer has since overwritten is
        // accounted for by Record's overflow bookkeeping, so at worst this writes a torn tail — which
        // the dropped count already marks as untrustworthy.
        for (long i = read; i < written; i++)
        {
            ref readonly var s = ref _buffer[i & _mask];
            _pending.Append(s.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(s.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(s.TimestampMicroseconds.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(s.IsValid ? '1' : '0')
                    .Append('\n');
        }

        if (_pending.Length == 0) return;

        AppendWithRotation(_pending.ToString());
        _headerWritten = true;

        // Never move the cursor BACKWARDS. `written` was sampled at the top of this method; if the
        // producer overflowed the ring while the append was on the disk, Record has already advanced
        // _read past it to account for the drop. An unconditional Exchange would rewind the cursor onto
        // slots the producer has since overwritten, and the next drain would re-emit that stale span.
        //
        // A compare-and-swap rather than a read-then-Exchange under an `if`: the producer can advance
        // _read in the window between those two, and a plain guarded Exchange would clobber that advance
        // exactly as the unguarded one did. The loop re-reads and retries, so the cursor lands on
        // whichever of the two is further ahead however the two threads interleave.
        //
        // NOT test-covered, deliberately. The rewind's effect IS visible in the file — the next drain
        // re-emits a stale span — but only inside a region the design already declares untrustworthy:
        // a rewind can only follow an overflow, and during overflow this drain writes a torn tail
        // regardless. So there is no contract a content assertion could be written against; one was
        // tried and failed against the CORRECT code. Observing the cursor mid-drain instead needs a
        // scheduling seam this class does not have; that was tried too and never caught the broken
        // version. Shipping either would have been worse than shipping none.
        while (true)
        {
            long current = Interlocked.Read(ref _read);
            if (current >= written) break;
            if (Interlocked.CompareExchange(ref _read, written, current) == current) break;
        }
    }

    /// <summary>
    /// Appends the closing trailer. Writer thread only — it owns the file, so a trailer written from
    /// the disposing thread would race the final drain and could land mid-line.
    /// </summary>
    private void WriteTrailer()
    {
        // No header means no file: a session that recorded nothing leaves nothing behind, and a file
        // containing only a trailer would be a new and pointless shape for the reader to handle.
        if (!_headerWritten) return;

        var trailer = new GazeRecordingTrailer(
            DroppedSamples: Interlocked.Read(ref _dropped),
            RecordedSamples: Interlocked.Read(ref _written));

        // Deliberately NOT AppendWithRotation: rotating here would move a file the operator is about to
        // go looking for, to save the few dozen bytes this line costs against a 16 MB cap.
        File.AppendAllText(
            _filePath,
            TrailerPrefix + " " + JsonSerializer.Serialize(trailer, HeaderJsonOptions) + "\n");
    }

    private void AppendWithRotation(string text)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var file = new FileInfo(_filePath);
        if (file.Exists && file.Length + text.Length > MaxRecordingBytes)
        {
            // Single generation, replaced — AdaptationLog's mechanism. The header is rewritten into the
            // new file so the rotated-to file is self-describing rather than a headless tail.
            File.Move(_filePath, _filePath + ".1", overwrite: true);
            _headerWritten = false;
            text = "# " + JsonSerializer.Serialize(_header, HeaderJsonOptions) + "\n" + text;
        }

        File.AppendAllText(_filePath, text);
    }

    private void SelfDisable(Exception ex)
    {
        _faulted = true;
        try { _onFault?.Invoke(ex); }
        catch { /* a broken reporter must not become a second fault */ }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Signals the writer to drain once more and joins it on a budget. Callers reach this from provider
    /// disposal, not from <c>Stop</c> — <see cref="RequestFlush"/> is the non-blocking path <c>Stop</c>
    /// uses, so shutdown is never spent waiting on a diagnostic's disk write.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopping = true;
        try { _wake.Set(); } catch (ObjectDisposedException) { /* already torn down */ }

        // Budgeted: the thread is IsBackground, so abandoning it cannot hold the process open. Losing
        // the tail of a recording is the acceptable outcome here; hanging the app's shutdown is not.
        //
        // The wake event is disposed ONLY when the join succeeded, i.e. when the writer is provably
        // finished with it. If the join timed out the writer is still running — stalled inside a file
        // write — and may yet touch the event, so it is deliberately leaked: one small handle on a
        // background thread that cannot keep the process alive, against the alternative of an
        // ObjectDisposedException killing the app at shutdown. The loop guards that race too; this
        // removes it rather than relying on the guard.
        bool joined = _writerThread.Join(ShutdownBudgetMs);
        if (joined)
            _wake.Dispose();

        // Reported after the join so the counts are final, and reported even when the join timed out —
        // an abandoned writer is precisely the case where the operator most needs to be told the
        // recording is incomplete, and it is also the case where the trailer may never reach the file.
        try
        {
            _onClosed?.Invoke(new GazeRecordingTrailer(
                DroppedSamples: Interlocked.Read(ref _dropped),
                RecordedSamples: Interlocked.Read(ref _written)));
        }
        catch { /* a broken reporter must not throw out of Dispose */ }
    }
}
