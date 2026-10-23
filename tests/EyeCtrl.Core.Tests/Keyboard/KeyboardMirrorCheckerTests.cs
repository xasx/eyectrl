namespace EyeCtrl.Core.Tests.Keyboard;

using System.Diagnostics;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using Microsoft.Extensions.Logging;

/// <summary>
/// E1's mirror check. The two properties that matter beyond the comparison itself: it never
/// blocks or delays the caller (a hide or an Enter), and it never logs a string — only a verdict
/// and lengths. The probe here can be made to hang, fail, or be a password field.
/// </summary>
public class KeyboardMirrorCheckerTests
{
    private sealed class Capture : ILogger
    {
        private readonly object _gate = new();
        public readonly List<string> Lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) Lines.Add(formatter(state, exception));
        }

        public string[] Snapshot() { lock (_gate) return Lines.ToArray(); }
    }

    private sealed class FakeProbe : IKeyboardContextProbe
    {
        public Func<int, string?> Read = _ => null;
        public int Reads;
        public string? GetForegroundProcessName() => "test.exe";

        public string? ReadFocusedTextTail(int maxChars)
        {
            Interlocked.Increment(ref Reads);
            return Read(maxChars);
        }
    }

    private sealed class FakeSensitive : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static KeyboardMirrorChecker New(Capture log, IKeyboardContextProbe? probe,
        ISensitiveContextDetector? sensitive = null, int timeBoxMs = 200) =>
        new(new KeyboardE1Recorder(log), probe, sensitive, TimeSpan.FromMilliseconds(timeBoxMs));

    // Only a HANG guard, never a timing assertion: the checker's work runs on the thread pool, and under a loaded parallel suite (CI, with
    // coverage) pool-thread injection alone can delay a continuation by seconds. Every ordering this file asserts is proved causally instead.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    // A hanging read waits longer than the hang guard, so a checker that waited for the read would fail the guard, not slip through it.
    private static readonly TimeSpan HangingReadWait = TimeSpan.FromSeconds(60);

    private static async Task Done(Task task) => await task.WaitAsync(HangGuard);

    [Fact]
    public async Task AMatchingTail_IsMatch_WithTheLengthsAndNoText()
    {
        var log = new Capture();
        var probe = new FakeProbe { Read = _ => "Hallo Wel" };

        await Done(New(log, probe).Begin("enter", "Hallo Wel"));

        Check.That(log.Snapshot()).ContainsExactly(
            "KB-E1 mirror at=enter res=match mirrorlen=9 ctllen=9 common=9");
    }

    [Fact]
    public async Task TheReadAsksForTheMirrorLengthPlusSlack()
    {
        var log = new Capture();
        int asked = 0;
        var probe = new FakeProbe { Read = n => { asked = n; return "xx"; } };

        await Done(New(log, probe).Begin("hide", "abcde"));

        Check.That(asked).IsEqualTo(7);
    }

    [Fact]
    public async Task ATrailingLineBreak_FromAnEnterThatAlreadyLanded_IsIgnored()
    {
        var log = new Capture();
        var probe = new FakeProbe { Read = _ => "Hallo Welt\r\n" };

        await Done(New(log, probe).Begin("enter", "Hallo Welt"));

        Check.That(log.Snapshot()[0]).Contains("res=match");
    }

    [Fact]
    public async Task ADifferingTail_IsMismatch_WithTheCommonSuffixLength()
    {
        var log = new Capture();
        var probe = new FakeProbe { Read = _ => "hallo Welt" };   // app auto-lowercased the H

        await Done(New(log, probe).Begin("hide", "Hallo Welt"));

        Check.That(log.Snapshot()).ContainsExactly(
            "KB-E1 mirror at=hide res=mismatch mirrorlen=10 ctllen=10 common=9");
    }

    [Fact]
    public async Task ATailLongerBefore_ThatStillEndsWithTheMirror_IsMatch()
    {
        var log = new Capture();
        var probe = new FakeProbe { Read = _ => "earlier text Hallo" };

        await Done(New(log, probe).Begin("hide", "Hallo"));

        Check.That(log.Snapshot()[0]).Contains("res=match");
    }

    [Fact]
    public async Task NoTextPattern_IsUnsupported()
    {
        var log = new Capture();
        var probe = new FakeProbe { Read = _ => null };

        await Done(New(log, probe).Begin("hide", "abc"));

        Check.That(log.Snapshot()).ContainsExactly(
            "KB-E1 mirror at=hide res=unsupported mirrorlen=3 ctllen=0 common=0");
    }

    [Fact]
    public async Task AnExceptionFromTheProbe_IsUnsupported_AndNeverEscapes()
    {
        var log = new Capture();
        var probe = new FakeProbe { Read = _ => throw new InvalidOperationException("COM fault") };

        await Done(New(log, probe).Begin("enter", "abc"));

        Check.That(log.Snapshot()[0]).Contains("res=unsupported");
    }

    [Fact]
    public async Task ASensitiveContext_IsSuppressed_AndTheControlIsNeverRead()
    {
        var log = new Capture();
        var probe = new FakeProbe { Read = _ => "hunter2" };

        await Done(New(log, probe, new FakeSensitive { IsSensitiveActive = true }).Begin("enter", "hunter2"));

        Check.That(probe.Reads).IsEqualTo(0);
        Check.That(log.Snapshot()).ContainsExactly(
            "KB-E1 mirror at=enter res=suppressed mirrorlen=0 ctllen=0 common=0");   // not even the length
    }

    [Fact]
    public async Task AfterAMutedKey_TheMirrorIsTainted_UntilTheSessionEnds_EvenWhenFocusMovedOn()
    {
        // Focus can leave a password field by gaze, with no key the keyboard sees: the mirror still holds
        // what was typed there. A check after that must not report its length or read the control.
        var log = new Capture();
        var probe = new FakeProbe { Read = _ => "hunter2" };
        var recorder = new KeyboardE1Recorder(log) { IsSensitive = () => true };
        Check.That(recorder.Muted()).IsTrue();                   // a key typed inside the password field
        recorder.IsSensitive = () => false;                      // focus left the field
        var checker = new KeyboardMirrorChecker(recorder, probe, null, TimeSpan.FromSeconds(5));

        await Done(checker.Begin("hide", "hunter2"));

        Check.That(recorder.Tainted).IsTrue();
        Check.That(probe.Reads).IsEqualTo(0);
        Check.That(log.Snapshot().Where(l => l.StartsWith("KB-E1 mirror", StringComparison.Ordinal))).ContainsExactly(
            "KB-E1 mirror at=hide res=suppressed mirrorlen=0 ctllen=0 common=0");

        recorder.ClearTaint();                                   // the session ended: the mirror was cleared
        await Done(checker.Begin("hide", "ok"));
        Check.That(probe.Reads).IsEqualTo(1);
    }

    [Fact]
    public async Task AnEmptyMirror_IsEmpty_AndNothingIsRead()
    {
        var log = new Capture();
        var probe = new FakeProbe();

        await Done(New(log, probe).Begin("hide", ""));

        Check.That(probe.Reads).IsEqualTo(0);
        Check.That(log.Snapshot()[0]).Contains("res=empty");
    }

    [Fact]
    public async Task NoProbe_IsUnsupported()
    {
        var log = new Capture();

        await Done(New(log, probe: null).Begin("hide", "abc"));

        Check.That(log.Snapshot()[0]).Contains("res=unsupported");
    }

    [Fact]
    public async Task AHangingRead_NeverBlocksTheCaller_AndIsLoggedUnsupportedAtTheTimeBox()
    {
        // CAUSAL, not wall-clock (CI run 40010182746 measured 6.7 s of thread-pool injection, not the time box): the read cannot end
        // before `release` is set, so a verdict that arrives while it is unset came from the time box; and a Begin that returns while
        // the verdict is still outstanding did not wait for it. The 1 s time box only has to outlast the two lines after Begin.
        var log = new Capture();
        using var release = new ManualResetEventSlim(false);
        var readEnded = 0;
        var probe = new FakeProbe
        {
            Read = _ => { release.Wait(HangingReadWait); Volatile.Write(ref readEnded, 1); return "late"; },
        };
        var checker = New(log, probe, timeBoxMs: 1000);
        try
        {
            var task = checker.Begin("hide", "abc");

            // Begin returned without waiting: the verdict is still outstanding (nothing logged yet, the task not complete)...
            Check.WithCustomMessage("Begin must return before the verdict (the read is on the pool, not the caller)").That(task.IsCompleted).IsFalse();
            Check.That(log.Snapshot()).IsEmpty();

            // ...and the verdict comes while the read is STILL hanging: from the time box, not from the read ending.
            await Done(task);
            Check.WithCustomMessage("the verdict must not wait for the hanging read").That(Volatile.Read(ref readEnded)).IsEqualTo(0);
            Check.That(log.Snapshot()).ContainsExactly(
                "KB-E1 mirror at=hide res=unsupported mirrorlen=3 ctllen=0 common=0");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task WhileAnAbandonedReadIsStillRunning_ANewCheckDoesNotStartAnotherRead()
    {
        var log = new Capture();
        using var release = new ManualResetEventSlim(false);
        var probe = new FakeProbe { Read = _ => { release.Wait(HangingReadWait); return null; } };
        var checker = New(log, probe, timeBoxMs: 50);

        try
        {
            await Done(checker.Begin("hide", "abc"));          // times out, the read is still hanging
            await Done(checker.Begin("enter", "abc"));         // must not pile on a second read
        }
        finally
        {
            release.Set();
        }

        Check.That(probe.Reads).IsEqualTo(1);
        Check.That(log.Snapshot()).HasSize(2);
        Check.That(log.Snapshot().All(l => l.Contains("res=unsupported"))).IsTrue();
    }

    [Fact]
    public async Task OnceTheHungReadEnds_ALaterCheckReadsAgain()
    {
        var log = new Capture();
        using var release = new ManualResetEventSlim(false);
        var calls = 0;
        var probe = new FakeProbe
        {
            Read = _ =>
            {
                if (Interlocked.Increment(ref calls) == 1) release.Wait(HangingReadWait);
                return "abc";
            },
        };
        var checker = New(log, probe, timeBoxMs: 50);

        try
        {
            await Done(checker.Begin("hide", "abc"));
        }
        finally
        {
            release.Set();
        }
        // The slot frees when the read itself ends, in a pool continuation: poll for it under the HANG guard only (a loaded pool can
        // take seconds to run that continuation; a fixed number of short tries was a wall-clock bound in disguise).
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref probe.Reads) == 1 && sw.Elapsed < HangGuard)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            await Done(checker.Begin("enter", "abc"));
        }

        Check.That(probe.Reads).IsStrictlyGreaterThan(1);
    }

    [Fact]
    public void Compare_IsPure_AndNeverReturnsTheStrings()
    {
        var (result, controlLength, common) = KeyboardMirrorChecker.Compare("Zebrafink", "Zebrafonk");

        Check.That(result).IsEqualTo("mismatch");
        Check.That(controlLength).IsEqualTo(9);
        Check.That(common).IsEqualTo(2);   // only the final "nk" agrees
    }
}
