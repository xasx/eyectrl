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

    private static async Task Done(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

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
            "KB-E1 mirror at=enter res=suppressed mirrorlen=7 ctllen=0 common=0");
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
        var log = new Capture();
        using var release = new ManualResetEventSlim(false);
        var probe = new FakeProbe { Read = _ => { release.Wait(TimeSpan.FromSeconds(30)); return "late"; } };
        var checker = New(log, probe, timeBoxMs: 100);

        var sw = Stopwatch.StartNew();
        var task = checker.Begin("hide", "abc");
        var returnedAfter = sw.ElapsedMilliseconds;
        await Done(task);
        var doneAfter = sw.ElapsedMilliseconds;
        release.Set();

        // Begin returned at once (the hang is on the pool, not the caller)...
        Check.That(returnedAfter).IsStrictlyLessThan(50L);
        // ...and the verdict came at the time box, not when the read finally ended.
        Check.That(doneAfter).IsStrictlyLessThan(5000L);
        Check.That(log.Snapshot()).ContainsExactly(
            "KB-E1 mirror at=hide res=unsupported mirrorlen=3 ctllen=0 common=0");
    }

    [Fact]
    public async Task WhileAnAbandonedReadIsStillRunning_ANewCheckDoesNotStartAnotherRead()
    {
        var log = new Capture();
        using var release = new ManualResetEventSlim(false);
        var probe = new FakeProbe { Read = _ => { release.Wait(TimeSpan.FromSeconds(30)); return null; } };
        var checker = New(log, probe, timeBoxMs: 50);

        await Done(checker.Begin("hide", "abc"));          // times out, the read is still hanging
        await Done(checker.Begin("enter", "abc"));         // must not pile on a second read
        release.Set();

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
                if (Interlocked.Increment(ref calls) == 1) release.Wait(TimeSpan.FromSeconds(30));
                return "abc";
            },
        };
        var checker = New(log, probe, timeBoxMs: 50);

        await Done(checker.Begin("hide", "abc"));
        release.Set();
        // The slot frees when the read itself ends; give the continuation a moment.
        for (var i = 0; i < 100 && probe.Reads == 1; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            await Done(checker.Begin("enter", "abc"));
            if (probe.Reads > 1) break;
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
