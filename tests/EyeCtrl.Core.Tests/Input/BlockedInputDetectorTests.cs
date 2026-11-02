namespace EyeCtrl.Core.Tests.Input;

using EyeCtrl.Core.Input;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// C10's R2 detector: the theory-free rule that a click/scroll whose leading absolute move did not
/// move the cursor means Windows is discarding injected input. The pins below are the ticket's
/// acceptance list — two consecutive misses block, the first verified move clears, an external
/// cursor writer neither raises nor clears, and the FreeLook sampler needs three seconds of
/// commanded-but-static cursor before it fires.
/// </summary>
public class BlockedInputDetectorTests
{
    private const int SelfMedium = 0x2000;
    private const int SelfHigh = 0x3000;

    [Fact]
    public void TwoConsecutiveActionsThatDoNotMoveTheCursor_Block()
    {
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);
        var stuck = new PhysicalPoint(300, 300);

        h.Detector.ReportAction(target, stuck, stuck);
        Check.WithCustomMessage("one dropped action is not enough evidence.")
            .That(h.Detector.IsBlocked).IsFalse();

        h.Detector.ReportAction(target, stuck, stuck);
        Check.That(h.Detector.IsBlocked).IsTrue();
        Check.That(h.Events).HasSize(1);
        Check.That(h.Events[0].IsBlocked).IsTrue();
    }

    [Fact]
    public void TheFirstVerifiedActionOfABlockedState_ClearsIt()
    {
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);
        var stuck = new PhysicalPoint(300, 300);

        h.Detector.ReportAction(target, stuck, stuck);
        h.Detector.ReportAction(target, stuck, stuck);
        Check.That(h.Detector.IsBlocked).IsTrue();

        h.Detector.ReportAction(target, stuck, target);

        Check.That(h.Detector.IsBlocked).IsFalse();
        Check.That(h.Events).HasSize(2);
        Check.That(h.Events[1].IsBlocked).IsFalse();
        Check.That(h.Detector.CurrentState.Trigger).IsEqualTo("verified action move");
    }

    [Fact]
    public void AnExternalWriterThatMovesTheCursorSomewhereElse_NeverBlocks()
    {
        // TD Control (or any other cursor writer) breaks the "after == before" condition. It is not
        // evidence of a UIPI drop, so it must not raise the banner however often it happens.
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);

        for (var i = 0; i < 5; i++)
            h.Detector.ReportAction(target, new PhysicalPoint(300, 300), new PhysicalPoint(320, 300));

        Check.That(h.Detector.IsBlocked).IsFalse();
        Check.That(h.Events).IsEmpty();
    }

    [Fact]
    public void AnActionWhoseCursorWasAlreadyAtTheTarget_IsNoEvidence()
    {
        // The move cannot be observed when the pointer starts where it is going: after == before is
        // the intended outcome, not a drop.
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);

        for (var i = 0; i < 5; i++)
            h.Detector.ReportAction(target, target, target);

        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void AnUnreadableCursor_IsNoEvidence()
    {
        var h = new Harness(cursor: null);

        for (var i = 0; i < 5; i++)
            h.Detector.ReportAction(new PhysicalPoint(10, 10), null, null);

        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void TheFreeLookSampler_BlocksAfterThreeStaticSeconds()
    {
        var h = new Harness();
        var elsewhere = new PhysicalPoint(10, 10);

        h.Detector.ReportMoveCommand(elsewhere); // opens the window at t=0
        h.Clock.Advance(500);
        h.Detector.ReportMoveCommand(elsewhere);
        h.Clock.Advance(500);
        h.Detector.ReportMoveCommand(elsewhere); // t=1000: sample 1
        h.Clock.Advance(1000);
        h.Detector.ReportMoveCommand(elsewhere); // t=2000: sample 2
        Check.WithCustomMessage("two static samples are not three.")
            .That(h.Detector.IsBlocked).IsFalse();

        h.Clock.Advance(1000);
        h.Detector.ReportMoveCommand(elsewhere); // t=3000: sample 3

        Check.That(h.Detector.IsBlocked).IsTrue();
        Check.That(h.Events).HasSize(1);
        Check.That(h.Events[0].Trigger).Contains("FreeLook");
    }

    [Fact]
    public void TheFreeLookSampler_DoesNotBlockWhileTheGazeIsStill()
    {
        // A still gaze commands the point the cursor is already on. Nothing was asked to move, so a
        // static cursor is not evidence — this is the ordinary "looking at one spot" case.
        var h = new Harness();
        var atCursor = new PhysicalPoint(300, 300);

        for (var i = 0; i < 6; i++)
        {
            h.Detector.ReportMoveCommand(atCursor);
            h.Clock.Advance(1000);
        }

        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void TheFreeLookSampler_ClearsWhenACommandedPointIsReached()
    {
        var h = new Harness();
        var stuck = new PhysicalPoint(300, 300);
        var elsewhere = new PhysicalPoint(10, 10);

        h.Detector.ReportMoveCommand(elsewhere);
        for (var i = 0; i < 3; i++)
        {
            h.Clock.Advance(1000);
            h.Detector.ReportMoveCommand(elsewhere);
        }
        Check.That(h.Detector.IsBlocked).IsTrue();

        // The next window's cursor read lands on the commanded point: a verified move.
        h.Cursor.Position = elsewhere;
        h.Clock.Advance(1000);
        h.Detector.ReportMoveCommand(elsewhere);

        Check.That(h.Detector.IsBlocked).IsFalse();
        Check.That(h.Events[^1].IsBlocked).IsFalse();
    }

    [Fact]
    public void TheFreeLookSampler_NeverBlocksOnAnExternalWriter()
    {
        // The cursor moves every second, just never to a point EyeCtrl commanded.
        var h = new Harness();
        var elsewhere = new PhysicalPoint(10, 10);

        h.Detector.ReportMoveCommand(elsewhere);
        for (var i = 0; i < 5; i++)
        {
            h.Cursor.Position = new PhysicalPoint(500 + i * 20, 500);
            h.Clock.Advance(1000);
            h.Detector.ReportMoveCommand(elsewhere);
        }

        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void TheBlockedState_CarriesTheHigherIntegrityForegroundReason()
    {
        var h = new Harness();
        h.Integrity.Foreground = Facts("Taskmgr.exe", SelfHigh, elevated: true);

        h.Detector.ReportAction(new PhysicalPoint(10, 10), new PhysicalPoint(300, 300), new PhysicalPoint(300, 300));
        h.Detector.ReportAction(new PhysicalPoint(10, 10), new PhysicalPoint(300, 300), new PhysicalPoint(300, 300));

        Check.That(h.Detector.CurrentState.Reason).IsEqualTo(BlockedInputReason.HigherIntegrityForeground);
        Check.That(h.Detector.CurrentState.Foreground.ProcessName).IsEqualTo("Taskmgr.exe");
    }

    [Fact]
    public void TheBlockedState_CarriesTheHigherIntegrityTargetReason()
    {
        var h = new Harness();
        h.Integrity.Point = Facts("setup.exe", SelfHigh, elevated: true);

        h.Detector.ReportAction(new PhysicalPoint(10, 10), new PhysicalPoint(300, 300), new PhysicalPoint(300, 300));
        h.Detector.ReportAction(new PhysicalPoint(10, 10), new PhysicalPoint(300, 300), new PhysicalPoint(300, 300));

        Check.That(h.Detector.CurrentState.Reason).IsEqualTo(BlockedInputReason.HigherIntegrityTarget);
    }

    [Fact]
    public void BlockAndClear_EachWriteOneWarningWithTheForegroundCursorAndTarget()
    {
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);
        var stuck = new PhysicalPoint(300, 300);
        h.Integrity.Foreground = Facts("Taskmgr.exe", SelfHigh, elevated: true);

        h.Detector.ReportAction(target, stuck, stuck);
        h.Detector.ReportAction(target, stuck, stuck);
        h.Detector.ReportAction(target, stuck, target);

        Check.That(h.Log.Entries).HasSize(2);
        Check.That(h.Log.Entries[0].Level).IsEqualTo(LogLevel.Warning);
        Check.That(h.Log.Entries[0].Message).Contains("Blocked input");
        Check.That(h.Log.Entries[0].Message).Contains("Taskmgr.exe");
        Check.That(h.Log.Entries[0].Message).Contains("IL=High");
        Check.That(h.Log.Entries[0].Message).Contains("(300,300)");
        Check.That(h.Log.Entries[0].Message).Contains("(10,10)");

        Check.That(h.Log.Entries[1].Level).IsEqualTo(LogLevel.Warning);
        Check.That(h.Log.Entries[1].Message).Contains("cleared");
    }

    [Fact]
    public void AUIAccessBuild_IsNotBlockedByAnElevatedForeground()
    {
        // C9's measured TD Control shape: High IL + UIAccess reaches elevated windows. The detector
        // keeps R2's trigger but must not name the elevated window when the token can reach it.
        var h = new Harness();
        h.Integrity.Self = new SelfTokenFacts(SelfHigh, Elevated: false, HasUIAccess: true);
        h.Integrity.Foreground = Facts("Taskmgr.exe", SelfHigh, elevated: true);

        h.Detector.ReportAction(new PhysicalPoint(10, 10), new PhysicalPoint(300, 300), new PhysicalPoint(300, 300));
        h.Detector.ReportAction(new PhysicalPoint(10, 10), new PhysicalPoint(300, 300), new PhysicalPoint(300, 300));

        Check.That(h.Detector.IsBlocked).IsTrue();
        Check.That(h.Detector.CurrentState.Reason).IsEqualTo(BlockedInputReason.Unknown);
    }

    private static ProcessTokenFacts Facts(string name, int level, bool elevated) =>
        new(name, level, elevated, HasUIAccess: false, AccessDenied: false);

    private sealed class Harness
    {
        public Harness(PhysicalPoint? cursor = null)
        {
            Cursor = new FakeCursor(cursor ?? new PhysicalPoint(300, 300));
            Integrity = new FakeIntegrity();
            Clock = new FakeClock();
            Log = new RecordingLogger();
            Detector = new BlockedInputDetector(Cursor, Integrity, Log, () => Clock.NowMs);
            Detector.StateChanged += (_, state) => Events.Add(state);
        }

        public FakeCursor Cursor { get; }
        public FakeIntegrity Integrity { get; }
        public FakeClock Clock { get; }
        public RecordingLogger Log { get; }
        public BlockedInputDetector Detector { get; }
        public List<BlockedInputState> Events { get; } = new();
    }

    private sealed class FakeCursor : ICursorPositionProbe
    {
        public FakeCursor(PhysicalPoint? position) => Position = position;

        public PhysicalPoint? Position { get; set; }

        public PhysicalPoint? TryGetCursorPosition() => Position;
    }

    private sealed class FakeIntegrity : IInputIntegrityProbe
    {
        public SelfTokenFacts Self { get; set; } = new(SelfMedium, Elevated: false, HasUIAccess: false);

        public ProcessTokenFacts Foreground { get; set; } = ProcessTokenFacts.Unknown;

        public ProcessTokenFacts Point { get; set; } = ProcessTokenFacts.Unknown;

        public SelfTokenFacts ReadSelf() => Self;

        public ProcessTokenFacts InspectForeground() => Foreground;

        public ProcessTokenFacts InspectPoint(PhysicalPoint point) => Point;

        public ProcessTokenFacts InspectWindow(IntPtr hwnd) => ProcessTokenFacts.Unknown;
    }

    private sealed class FakeClock
    {
        public long NowMs { get; private set; }

        public void Advance(long milliseconds) => NowMs += milliseconds;
    }

    private sealed class RecordingLogger : ILogger<BlockedInputDetector>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
