namespace EyeCtrl.Core.Tests.Input;

using EyeCtrl.Core.Input;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

/// <summary>
/// Ported from review-02's probes as permanent regression coverage for N1. P7 is the trap: the
/// cursor read right after the click lags by one frame, the owner is still looking at the commit
/// button, and FreeLook moves the cursor back there before the 40 ms re-read. The pre-move read must
/// see the click's landing instead, or two landed clicks raise a false banner. P7b is the same
/// pattern with the gaze moving away; P8 is the synchronous-read control; P9 is a real block with
/// the gaze pinned to the frozen cursor and must still block.
/// </summary>
public class BlockedInputR02ProbesTests
{
    private static readonly PhysicalPoint CommitButton = new(2000, 1200);

    [Fact]
    public void P7_LaggingRead_GazeStillOnTheCommitButton_DoesNotBlock()
    {
        var h = new Rig(lagging: true);
        h.Click(new PhysicalPoint(600, 2100));
        h.Click(new PhysicalPoint(3000, 500));
        h.Click(new PhysicalPoint(900, 300));

        Check.WithCustomMessage("every click landed; FreeLook merely moved the cursor back to the gaze")
            .That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void P7b_LaggingRead_GazeMovesAway_DoesNotBlock()
    {
        var h = new Rig(lagging: true, gazeAway: true);
        h.Click(new PhysicalPoint(600, 2100));
        h.Click(new PhysicalPoint(3000, 500));
        h.Click(new PhysicalPoint(900, 300));

        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void P8_SyncRead_GazeStillOnTheCommitButton_DoesNotBlock()
    {
        var h = new Rig(lagging: false);
        h.Click(new PhysicalPoint(600, 2100));
        h.Click(new PhysicalPoint(3000, 500));
        h.Click(new PhysicalPoint(900, 300));
        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void P9_RealBlock_GazeOnTheCommitButton_Blocks()
    {
        var h = new Rig(lagging: false, dropping: true);
        h.Click(new PhysicalPoint(600, 2100));
        h.Click(new PhysicalPoint(3000, 500));
        h.Click(new PhysicalPoint(900, 300));
        Check.That(h.Detector.IsBlocked).IsTrue();
    }

    private sealed class Rig
    {
        private long _clock;
        private readonly Cursor _cursor = new() { Position = CommitButton };
        private readonly Inner _inner;
        private readonly VerifiedInputSimulator _sim;
        private int _jitter;

        public Rig(bool lagging, bool dropping = false, bool gazeAway = false)
        {
            GazeAway = gazeAway;
            Detector = new BlockedInputDetector(
                _cursor, new NoIntegrity(), null, () => _clock, () => (3840, 2400));
            _inner = new Inner(_cursor, lagging, dropping);
            _sim = new VerifiedInputSimulator(_inner, Detector, _cursor);
            for (var i = 0; i < 40; i++) Frame();
        }

        public BlockedInputDetector Detector { get; }

        private bool GazeAway { get; }

        public void Click(PhysicalPoint p)
        {
            _sim.LeftClick(p);
            for (var i = 0; i < 20; i++) Frame(); // ~600 ms of FreeLook on the commit button
        }

        private void Frame()
        {
            _clock += 30;
            _inner.Flush();
            _jitter = (_jitter + 1) % 3;
            var gaze = GazeAway
                ? new PhysicalPoint(CommitButton.X + 400, CommitButton.Y)
                : new PhysicalPoint(CommitButton.X + _jitter - 1, CommitButton.Y);
            _sim.MoveCursor(gaze);
        }
    }

    private sealed class Cursor : ICursorPositionProbe
    {
        public PhysicalPoint? Position { get; set; }
        public PhysicalPoint? TryGetCursorPosition() => Position;
    }

    private sealed class NoIntegrity : IInputIntegrityProbe
    {
        public SelfTokenFacts ReadSelf() => new(0x2000, false, false);
        public ProcessTokenFacts InspectForeground() => ProcessTokenFacts.Unknown;
        public ProcessTokenFacts InspectPoint(PhysicalPoint point) => ProcessTokenFacts.Unknown;
        public ProcessTokenFacts InspectWindow(IntPtr hwnd) => ProcessTokenFacts.Unknown;
    }

    private sealed class Inner(Cursor cursor, bool lagging, bool dropping) : IInputSimulator
    {
        private PhysicalPoint? _pending;

        private void Land(PhysicalPoint p)
        {
            if (dropping) return;
            if (lagging) _pending = p; else cursor.Position = p;
        }

        public void Flush() { if (_pending is { } p) cursor.Position = p; _pending = null; }

        public void MoveCursor(PhysicalPoint point) { Flush(); Land(point); if (lagging) Flush(); }
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
}
