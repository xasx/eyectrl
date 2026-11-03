namespace EyeCtrl.Core.Tests.Input;

using EyeCtrl.Core.Input;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

/// <summary>
/// Ported from review-01's read-only probes (P1-P6) as permanent regression coverage. P1/P2 pin the
/// F2 screen clamp, P3 pins F1's delayed confirmation of a miss, P4 pins F5's no-false-clear rule,
/// P5 pins external-writer immunity, and P6 replays the 02.11 sitting. P4's setup advances past the
/// confirmation delay (the reviewer's original asserted the block before the re-read).
/// </summary>
public class BlockedInputReviewProbesTests
{
    private const int W = 3840, H = 2400;

    // P6: the 02.11 sitting, replayed: the cursor frozen at (1937,709), FreeLook moves dropped,
    // the log's two clicks at 23:24:38 and :45. R2 must block on the second click at the latest.
    [Fact]
    public void P6_Sitting0211_BlocksByTheSecondClick()
    {
        var clock = 0L;
        var cursor = new Cursor { Position = new PhysicalPoint(1937, 709) };
        var det = new BlockedInputDetector(cursor, new NoIntegrity(), null, () => clock);
        var inner = new DroppingInner(); // every event is dropped: the cursor never moves
        var sim = new VerifiedInputSimulator(inner, det, cursor);

        // 23:24:19 → 23:24:38: FreeLook over the note area, ~33 Hz.
        for (var i = 0; i < 19 * 33; i++)
        {
            clock += 30;
            sim.MoveCursor(new PhysicalPoint(850 + (i % 7), 290 + (i % 5)));
        }
        var blockedBeforeClicks = det.IsBlocked;

        sim.LeftClick(new PhysicalPoint(858, 286));
        clock += 7600;
        sim.LeftClick(new PhysicalPoint(853, 246));

        Check.WithCustomMessage($"sampler alone blocked first: {blockedBeforeClicks}")
            .That(det.IsBlocked).IsTrue();
    }

    // P1: the owner looks just above the top edge (the docked toolbar's summon band) for 3.5 s.
    // The OS clamps the cursor to y=0; nothing is being dropped.
    [Fact]
    public void P1_OffScreenGazeAboveTheTopEdge_DoesNotBlock()
    {
        var clock = 0L;
        var cursor = new Cursor { Position = new PhysicalPoint(1900, 0) };
        var det = new BlockedInputDetector(cursor, new NoIntegrity(), null, () => clock);
        var sim = new VerifiedInputSimulator(new ClampingInner(cursor), det, cursor);

        for (var i = 0; i < 3500 / 30; i++)
        {
            clock += 30;
            sim.MoveCursor(new PhysicalPoint(1900 + (i % 3), -30 - (i % 4)));
        }

        Check.WithCustomMessage("a clamped (off-screen) command is not a dropped move")
            .That(det.IsBlocked).IsFalse();
    }

    // P2: two clicks aimed 20 px above the top edge with the cursor already clamped there.
    [Fact]
    public void P2_TwoClicksBeyondTheEdge_DoNotBlock()
    {
        var cursor = new Cursor { Position = new PhysicalPoint(3605, 0) };
        var det = new BlockedInputDetector(cursor, new NoIntegrity(), null, () => 0);
        var sim = new VerifiedInputSimulator(new ClampingInner(cursor), det, cursor);

        sim.LeftClick(new PhysicalPoint(3605, -20));
        sim.LeftClick(new PhysicalPoint(3605, -20));

        Check.WithCustomMessage("the clamped landing point was reached")
            .That(det.IsBlocked).IsFalse();
    }

    // P3: if GetCursorPos lags SendInput (the move is applied by another thread just after the
    // call returns), two ordinary clicks whose cursor started elsewhere read as two misses.
    [Fact]
    public void P3_ACursorReadThatLagsTheInjection_DoesNotBlock()
    {
        var clock = 0L;
        var cursor = new Cursor { Position = new PhysicalPoint(2000, 1200) };
        var det = new BlockedInputDetector(cursor, new NoIntegrity(), null, () => clock);
        var inner = new LaggingInner(cursor);
        var sim = new VerifiedInputSimulator(inner, det, cursor);

        // The click's own read lags by a frame. The next gaze frame advances the clock past the
        // confirmation delay, but its PRE-MOVE read sees the click's landing and drops the candidate
        // (N1). This is the non-vacuous version of the r01 probe (the original never advanced time).
        sim.LeftClick(new PhysicalPoint(600, 2100));
        inner.Flush(); // the click's move lands
        clock += 30;
        sim.MoveCursor(new PhysicalPoint(2000, 1200)); // pre-move read: (600, 2100); FreeLook returns
        inner.Flush();

        sim.LeftClick(new PhysicalPoint(3000, 500));
        inner.Flush();
        clock += 60; // past this candidate's due time
        sim.MoveCursor(new PhysicalPoint(2000, 1200));
        inner.Flush();

        Check.WithCustomMessage("every move landed, only a few microseconds late")
            .That(det.IsBlocked).IsFalse();
    }

    // P4: a real block (cursor frozen) while the gaze sweeps across the frozen cursor's spot at a
    // sampler boundary: the sampler calls it "reached" and clears the banner.
    [Fact]
    public void P4_AGazeSweepOverTheFrozenCursor_DoesNotClearARealBlock()
    {
        var clock = 0L;
        var frozen = new PhysicalPoint(1937, 709);
        var cursor = new Cursor { Position = frozen };
        var det = new BlockedInputDetector(cursor, new NoIntegrity(), null, () => clock);
        var sim = new VerifiedInputSimulator(new DroppingInner(), det, cursor);
        sim.LeftClick(new PhysicalPoint(600, 2100));
        sim.LeftClick(new PhysicalPoint(600, 2100));

        // c10 fixup-01 F1: the miss is confirmed by a re-read after ~40 ms, so let the pending
        // candidates confirm before asserting the block (the reviewer's probe predates the delay).
        clock += 50;
        sim.MoveCursor(frozen);
        Check.That(det.IsBlocked).IsTrue();

        for (var i = 0; i < 30; i++) { clock += 30; sim.MoveCursor(new PhysicalPoint(600 + 40 * i, 2100 - 40 * i)); }
        clock += 30; sim.MoveCursor(new PhysicalPoint(1930, 712)); // passes over the frozen cursor at the boundary
        clock += 30; sim.MoveCursor(new PhysicalPoint(1990, 760));
        clock += 1000; sim.MoveCursor(new PhysicalPoint(2050, 800));

        Check.WithCustomMessage("the frozen cursor merely lay on the gaze path")
            .That(det.IsBlocked).IsTrue();
    }

    // P5: miss, external-writer action, miss must not block (the external branch resets the streak).
    [Fact]
    public void P5_AnExternalWriterBetweenTwoMisses_BreaksTheStreak()
    {
        var cursor = new Cursor();
        var det = new BlockedInputDetector(cursor, new NoIntegrity(), null, () => 0);
        var t = new PhysicalPoint(10, 10);
        det.ReportAction(t, new PhysicalPoint(300, 300), new PhysicalPoint(300, 300));
        det.ReportAction(t, new PhysicalPoint(300, 300), new PhysicalPoint(400, 300));
        det.ReportAction(t, new PhysicalPoint(400, 300), new PhysicalPoint(400, 300));
        Check.That(det.IsBlocked).IsFalse();
    }

    private sealed class Cursor : ICursorPositionProbe
    {
        public PhysicalPoint? Position { get; set; } = new PhysicalPoint(300, 300);
        public PhysicalPoint? TryGetCursorPosition() => Position;
    }

    private sealed class NoIntegrity : IInputIntegrityProbe
    {
        public SelfTokenFacts ReadSelf() => new(0x2000, false, false);
        public ProcessTokenFacts InspectForeground() => ProcessTokenFacts.Unknown;
        public ProcessTokenFacts InspectPoint(PhysicalPoint point) => ProcessTokenFacts.Unknown;
        public ProcessTokenFacts InspectWindow(IntPtr hwnd) => ProcessTokenFacts.Unknown;
    }

    private class Inner : IInputSimulator
    {
        protected virtual void Land(PhysicalPoint p) { }
        public void MoveCursor(PhysicalPoint point) => Land(point);
        public void LeftClick(PhysicalPoint point) => Land(point);
        public void RightClick(PhysicalPoint point) => Land(point);
        public void DoubleClick(PhysicalPoint point) => Land(point);
        public void MouseDown(PhysicalPoint point) => Land(point);
        public void MouseUp(PhysicalPoint point) => Land(point);
        public void Scroll(PhysicalPoint point, int deltaX, int deltaY) { }
        public void ScrollAt(PhysicalPoint point, int deltaX, int deltaY) => Land(point);
        public void SendKeyPress(ushort virtualKeyCode) { }
        public void SendKeyDown(ushort virtualKeyCode) { }
        public void SendKeyUp(ushort virtualKeyCode) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }

    private sealed class DroppingInner : Inner { }

    private sealed class ClampingInner(Cursor cursor) : Inner
    {
        protected override void Land(PhysicalPoint p) =>
            cursor.Position = new PhysicalPoint(Math.Clamp(p.X, 0, W - 1), Math.Clamp(p.Y, 0, H - 1));
    }

    private sealed class LaggingInner(Cursor cursor) : Inner
    {
        private PhysicalPoint? _pending;
        protected override void Land(PhysicalPoint p) => _pending = p;
        public void Flush() { if (_pending is { } p) cursor.Position = p; _pending = null; }
    }
}
