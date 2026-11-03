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
        h.ConfirmMisses();
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
        h.ConfirmMisses();
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
        h.ConfirmMisses();

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
        h.ConfirmMisses();

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
        h.ConfirmMisses();
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
        h.ConfirmMisses();

        Check.That(h.Detector.IsBlocked).IsTrue();
        Check.That(h.Detector.CurrentState.Reason).IsEqualTo(BlockedInputReason.Unknown);
    }

    [Fact]
    public void TheConfirmationWaitsTheFullDelay()
    {
        // F1m: with the delay mutated to 0 both candidates are due at 39 ms and the banner appears;
        // at the real 40 ms the two confirmations come one tick later.
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);
        var stuck = new PhysicalPoint(300, 300);

        h.Detector.ReportAction(target, stuck, stuck);
        h.Detector.ReportAction(target, stuck, stuck);

        // 39 ms is deliberately a literal: using the constant would shrink with the mutant and the
        // test would stay green at delay 0. At the real 40 both are still unconfirmed here.
        h.Clock.Advance(39);
        h.Detector.ReportMoveCommand(stuck);
        Check.WithCustomMessage("a miss was counted before the confirmation delay elapsed.")
            .That(h.Detector.IsBlocked).IsFalse();

        h.Clock.Advance(1);
        h.Detector.ReportMoveCommand(stuck);
        Check.That(h.Detector.IsBlocked).IsTrue();
    }

    [Fact]
    public void APreMoveReadAtTheClickTarget_DropsThePendingMiss()
    {
        // N1: the click landed late, and the next gaze frame's PRE-move read still sees it at the
        // target. That is positive evidence, not a drop.
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);
        var stuck = new PhysicalPoint(300, 300);

        h.Detector.ReportAction(target, stuck, stuck);
        h.Clock.Advance(BlockedInputDetector.MissConfirmationDelayMilliseconds);
        h.Detector.ReportMoveCommand(target, cursorBeforeMove: target);

        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void APreMoveReadStillAtTheOldSpot_KeepsThePendingMiss()
    {
        // N1/P9: the click was dropped; the pre-move read is still where the cursor was, so the
        // candidate is real. Two of them block exactly as before.
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);
        var stuck = new PhysicalPoint(300, 300);

        h.Detector.ReportAction(target, stuck, stuck);
        h.Clock.Advance(BlockedInputDetector.MissConfirmationDelayMilliseconds);
        h.Detector.ReportMoveCommand(target, cursorBeforeMove: stuck);
        Check.That(h.Detector.IsBlocked).IsFalse(); // one confirmed miss

        h.Detector.ReportAction(target, stuck, stuck);
        h.Clock.Advance(BlockedInputDetector.MissConfirmationDelayMilliseconds);
        h.Detector.ReportMoveCommand(target, cursorBeforeMove: stuck);
        Check.That(h.Detector.IsBlocked).IsTrue();
    }

    [Fact]
    public void APreMoveReadElsewhere_DropsThePendingMiss_AsAnExternalWriter()
    {
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);
        var stuck = new PhysicalPoint(300, 300);

        h.Detector.ReportAction(target, stuck, stuck);
        h.Clock.Advance(BlockedInputDetector.MissConfirmationDelayMilliseconds);
        h.Detector.ReportMoveCommand(target, cursorBeforeMove: new PhysicalPoint(800, 800));

        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void APreMoveReadThatFailed_DropsTheDueCandidate()
    {
        // A supplied null is "unreadable", not "read after the move": no evidence, no miss.
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);
        var stuck = new PhysicalPoint(300, 300);

        h.Detector.ReportAction(target, stuck, stuck);
        h.Clock.Advance(BlockedInputDetector.MissConfirmationDelayMilliseconds);
        h.Detector.ReportMoveCommand(target, cursorBeforeMove: null);

        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void OneConfirmedMiss_IsNotEnough()
    {
        // The debounce is two CONFIRMED misses (the first dropped click already did nothing); one
        // confirmed miss must not raise the banner (M1).
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);
        var stuck = new PhysicalPoint(300, 300);

        h.Detector.ReportAction(target, stuck, stuck);
        h.ConfirmMisses();
        Check.WithCustomMessage("one confirmed miss is not enough evidence.")
            .That(h.Detector.IsBlocked).IsFalse();

        h.Detector.ReportAction(target, stuck, stuck);
        h.ConfirmMisses();
        Check.That(h.Detector.IsBlocked).IsTrue();
    }

    [Fact]
    public void AMissIsNotCounted_UntilTheDelayedReReadConfirmsIt()
    {
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);
        var stuck = new PhysicalPoint(300, 300);

        h.Detector.ReportAction(target, stuck, stuck);
        Check.That(h.Detector.IsBlocked).IsFalse(); // candidate only

        h.Clock.Advance(BlockedInputDetector.MissConfirmationDelayMilliseconds - 1);
        h.Detector.ReportMoveCommand(stuck); // not due yet
        Check.That(h.Detector.IsBlocked).IsFalse();

        h.Clock.Advance(1);
        h.Detector.ReportAction(target, stuck, stuck); // processes the first candidate, then queues the second
        Check.That(h.Detector.IsBlocked).IsFalse();      // one confirmed miss

        h.Clock.Advance(BlockedInputDetector.MissConfirmationDelayMilliseconds);
        h.Detector.ReportMoveCommand(stuck);             // confirms the second
        Check.That(h.Detector.IsBlocked).IsTrue();
    }

    [Fact]
    public void ALandedMoveAfterTheReadDropsThePendingMiss()
    {
        // F1: the cursor read right after SendInput can lag. The re-read finds the pointer at P, so
        // the action was not dropped and must not count.
        var h = new Harness();
        var target = new PhysicalPoint(10, 10);
        var start = new PhysicalPoint(300, 300);

        h.Detector.ReportAction(target, start, start);
        h.Cursor.Position = target; // the move lands just after the read
        h.Clock.Advance(BlockedInputDetector.MissConfirmationDelayMilliseconds);
        h.Detector.ReportMoveCommand(target);

        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void AnOffScreenTarget_IsClampedBeforeComparing()
    {
        var h = new Harness(screen: (3840, 2400));
        var clampedEdge = new PhysicalPoint(3839, 0);

        // A corner caption at y=-20 with the cursor already clamped to the top edge is a reached
        // target, not a 20 px miss (F2).
        h.Detector.ReportAction(new PhysicalPoint(3839, -20), clampedEdge, clampedEdge);
        h.Detector.ReportAction(new PhysicalPoint(3839, -20), clampedEdge, clampedEdge);

        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void AnOffScreenFreeLookCommand_IsClampedBeforeTheSamplerCompares()
    {
        var h = new Harness(cursor: new PhysicalPoint(1900, 0), screen: (3840, 2400));

        for (var i = 0; i < 6; i++)
        {
            h.Detector.ReportMoveCommand(new PhysicalPoint(1900 + (i % 3), -30 - (i % 4)));
            h.Clock.Advance(1000);
        }

        Check.WithCustomMessage("a clamped gaze above the top edge is not a dropped move")
            .That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void AClickAtTheFrozenCursor_DoesNotClearARealBlock()
    {
        // F5: a click that lands where the frozen cursor already sits is no evidence that injection
        // works again; the clear needs an observed move.
        var h = new Harness();
        h.Integrity.Foreground = Facts("Taskmgr.exe", SelfHigh, elevated: true);
        // Open the sampler window, then three full sample windows.
        for (var i = 0; i < 4; i++)
        {
            h.Detector.ReportMoveCommand(new PhysicalPoint(10, 10));
            h.Clock.Advance(1000);
        }
        Check.That(h.Detector.IsBlocked).IsTrue();

        h.Detector.ReportAction(new PhysicalPoint(300, 300), new PhysicalPoint(300, 300), new PhysicalPoint(300, 300));

        Check.That(h.Detector.IsBlocked).IsTrue();
    }

    private static ProcessTokenFacts Facts(string name, int level, bool elevated) =>
        new(name, level, elevated, HasUIAccess: false, AccessDenied: false);

    private sealed class Harness
    {
        public Harness(PhysicalPoint? cursor = null, (int Width, int Height)? screen = null)
        {
            Cursor = new FakeCursor(cursor ?? new PhysicalPoint(300, 300));
            Integrity = new FakeIntegrity();
            Clock = new FakeClock();
            Log = new RecordingLogger();
            Detector = new BlockedInputDetector(
                Cursor, Integrity, Log, () => Clock.NowMs,
                screen is { } s ? () => s : null);
            Detector.StateChanged += (_, state) => Events.Add(state);
        }

        public FakeCursor Cursor { get; }
        public FakeIntegrity Integrity { get; }
        public FakeClock Clock { get; }
        public RecordingLogger Log { get; }
        public BlockedInputDetector Detector { get; }
        public List<BlockedInputState> Events { get; } = new();

        /// <summary>Advances past the confirmation delay and drains the pending misses.</summary>
        public void ConfirmMisses()
        {
            Clock.Advance(BlockedInputDetector.MissConfirmationDelayMilliseconds);
            Detector.ReportMoveCommand(Cursor.Position ?? new PhysicalPoint(300, 300));
        }
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
