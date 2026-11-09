namespace EyeCtrl.Core.Tests.Input;

using EyeCtrl.Core.Input;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

/// <summary>
/// C10's observation point. It must add R2's before/after read to exactly the injection shapes the
/// ticket names (click, double-click, pointed scroll) and feed every commanded move to the
/// FreeLook sampler, while drags, bare wheel scrolls and keyboard input pass through untouched.
/// </summary>
public class VerifiedInputSimulatorTests
{
    private static readonly PhysicalPoint Target = new(10, 10);
    private static readonly PhysicalPoint Start = new(300, 300);

    [Fact]
    public void AClickWhoseMoveLands_IsAReachedVerification()
    {
        var h = new Harness(moveOnClick: true);

        for (var i = 0; i < 5; i++)
            h.Simulator.LeftClick(Target);

        Check.That(h.Detector.IsBlocked).IsFalse();
        Check.That(h.Inner.LeftClicks).IsEqualTo(5);
    }

    [Fact]
    public void TwoClicksThatDoNotMoveTheCursor_Block()
    {
        var h = new Harness(moveOnClick: false);

        h.Simulator.LeftClick(Target);
        Check.That(h.Detector.IsBlocked).IsFalse();

        h.Simulator.LeftClick(Target);
        h.ConfirmMisses();
        Check.That(h.Detector.IsBlocked).IsTrue();
    }

    [Fact]
    public void ARightClickAndAMiddleClick_AreVerifiedToo()
    {
        var h = new Harness(moveOnClick: false);

        h.Simulator.RightClick(Target);
        h.Simulator.MiddleClick(Target);
        h.ConfirmMisses();

        Check.That(h.Detector.IsBlocked).IsTrue();
        Check.That(h.Inner.RightClicks).IsEqualTo(1);
        Check.That(h.Inner.MiddleClicks).IsEqualTo(1);
    }

    [Fact]
    public void ADoubleClick_IsOneVerifiedAction()
    {
        var h = new Harness(moveOnClick: false);

        h.Simulator.DoubleClick(Target);
        Check.That(h.Detector.IsBlocked).IsFalse(); // one batch, one observation

        h.Simulator.DoubleClick(Target);
        h.ConfirmMisses();
        Check.That(h.Detector.IsBlocked).IsTrue();
        Check.That(h.Inner.DoubleClicks).IsEqualTo(2);
    }

    [Fact]
    public void ScrollAt_IsVerified_ButTheBareWheelAndTheAtomicHopAreNot()
    {
        var h = new Harness(moveOnClick: false);

        // A bare wheel does not move the pointer by contract, and the hop restores it: neither can
        // claim the cursor should be somewhere else afterwards.
        for (var i = 0; i < 5; i++)
        {
            h.Simulator.Scroll(Target, 0, 1);
            h.Simulator.ScrollAtAndRestore(Target, Start, 0, 1);
        }
        Check.That(h.Detector.IsBlocked).IsFalse();

        h.Simulator.ScrollAt(Target, 0, 1);
        h.Simulator.ScrollAt(Target, 0, 1);
        h.ConfirmMisses();
        Check.That(h.Detector.IsBlocked).IsTrue();
    }

    [Fact]
    public void DragPressAndRelease_AreNotActionHits()
    {
        var h = new Harness(moveOnClick: false);

        for (var i = 0; i < 5; i++)
        {
            h.Simulator.MouseDown(Target);
            h.Simulator.MouseUp(Target);
        }

        Check.That(h.Detector.IsBlocked).IsFalse();
        Check.That(h.Inner.MouseDowns).IsEqualTo(5);
        Check.That(h.Inner.MouseUps).IsEqualTo(5);
    }

    [Fact]
    public void MoveCursor_FeedsTheFreeLookSampler()
    {
        var h = new Harness(moveOnClick: false);

        h.Simulator.MoveCursor(Target); // opens the sampler window at t=0
        for (var i = 0; i < 3; i++)
        {
            h.Clock.Advance(1000);
            h.Simulator.MoveCursor(Target);
        }

        Check.That(h.Detector.IsBlocked).IsTrue();
        Check.That(h.Inner.Moves).IsEqualTo(4);
    }

    [Fact]
    public void TheConfirmationRead_HappensAfterOurMove_SoALateLandingDoesNotBlock()
    {
        // L3's ordering, pinned at the decorator: the detector's post-move read must be taken after
        // _inner.MoveCursor. Two clicks missed while the cursor stayed at Start; on the due frame the
        // fake's MoveCursor lands the cursor on the commanded point, standing for the late landing
        // this frame's own move overwrites. The post-move read then sees the target, not `before`,
        // so neither candidate may become a miss. Reporting before the move would read Start twice
        // and block.
        var h = new Harness(moveOnClick: false, moveOnMove: true);

        h.Simulator.LeftClick(Target);
        h.Simulator.LeftClick(Target);

        h.Clock.Advance(BlockedInputDetector.MissConfirmationDelayMilliseconds);
        h.Simulator.MoveCursor(Target);

        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    [Fact]
    public void AModifiedWheelScroll_IsVerified_BecauseItsBatchLeadsWithAMove()
    {
        var h = new Harness(moveOnClick: false);
        var mods = new HashSet<ModifierKey> { ModifierKey.Ctrl };

        h.Simulator.Scroll(Target, 0, 1, mods);
        h.Simulator.Scroll(Target, 0, 1, mods);
        h.ConfirmMisses();
        Check.That(h.Detector.IsBlocked).IsTrue();
        Check.That(h.Inner.ModifiedScrolls).IsEqualTo(2);
    }

    [Fact]
    public void AModifiedWheelScrollWithNoModifiers_IsTheBareWheel_AndIsNotVerified()
    {
        var h = new Harness(moveOnClick: false);

        for (var i = 0; i < 5; i++)
            h.Simulator.Scroll(Target, 0, 1, modifiers: null);

        Check.That(h.Detector.IsBlocked).IsFalse();
        Check.That(h.Inner.ModifiedScrolls).IsEqualTo(5);
    }

    [Fact]
    public void KeyboardCalls_PassStraightThrough()
    {
        var h = new Harness(moveOnClick: false);
        var mods = new HashSet<ModifierKey> { ModifierKey.Shift };

        h.Simulator.SendKeyPress(0x41);
        h.Simulator.SendKeyDown(0x42);
        h.Simulator.SendKeyUp(0x42);
        h.Simulator.SendText("abc");
        h.Simulator.SendModifiedKeyPress(mods, 0x43);
        h.Simulator.SendKeyPress(0x44, isExtendedKey: true);
        h.Simulator.SendModifiedKeyPress(mods, 0x45, isExtendedKey: true);

        Check.That(h.Inner.KeyPresses).IsEqualTo(2);
        Check.That(h.Inner.KeyDowns).IsEqualTo(1);
        Check.That(h.Inner.KeyUps).IsEqualTo(1);
        Check.That(h.Inner.Texts).IsEqualTo(1);
        Check.That(h.Inner.ModifiedKeyPresses).IsEqualTo(2);
        Check.That(h.Detector.IsBlocked).IsFalse();
    }

    private sealed class Harness
    {
        public Harness(bool moveOnClick, bool moveOnMove = false)
        {
            Cursor = new FakeCursor(Start);
            Inner = new FakeInput(Cursor, moveOnClick, moveOnMove);
            Clock = new FakeClock();
            Detector = new BlockedInputDetector(
                Cursor, new FakeIntegrity(), nowMilliseconds: () => Clock.NowMs);
            Simulator = new VerifiedInputSimulator(Inner, Detector, Cursor);
        }

        public FakeCursor Cursor { get; }
        public FakeInput Inner { get; }
        public FakeClock Clock { get; }
        public BlockedInputDetector Detector { get; }
        public VerifiedInputSimulator Simulator { get; }

        /// <summary>Advances past the confirmation delay and drains the pending misses.</summary>
        public void ConfirmMisses()
        {
            Clock.Advance(BlockedInputDetector.MissConfirmationDelayMilliseconds);
            Simulator.MoveCursor(Cursor.Position ?? Start);
        }
    }

    private sealed class FakeCursor : ICursorPositionProbe
    {
        public FakeCursor(PhysicalPoint position) => Position = position;

        public PhysicalPoint? Position { get; set; }

        public PhysicalPoint? TryGetCursorPosition() => Position;
    }

    private sealed class FakeInput : IInputSimulator
    {
        private readonly FakeCursor _cursor;
        private readonly bool _moveOnClick;
        private readonly bool _moveOnMove;

        public FakeInput(FakeCursor cursor, bool moveOnClick, bool moveOnMove)
        {
            _cursor = cursor;
            _moveOnClick = moveOnClick;
            _moveOnMove = moveOnMove;
        }

        public int LeftClicks { get; private set; }
        public int RightClicks { get; private set; }
        public int MiddleClicks { get; private set; }
        public int DoubleClicks { get; private set; }
        public int MouseDowns { get; private set; }
        public int MouseUps { get; private set; }
        public int Moves { get; private set; }
        public int ModifiedScrolls { get; private set; }
        public int KeyPresses { get; private set; }
        public int KeyDowns { get; private set; }
        public int KeyUps { get; private set; }
        public int Texts { get; private set; }
        public int ModifiedKeyPresses { get; private set; }

        private void Land(PhysicalPoint point)
        {
            if (_moveOnClick) _cursor.Position = point;
        }

        public void MoveCursor(PhysicalPoint point)
        {
            Moves++;
            // moveOnMove models a successful absolute move: the cursor lands on the commanded point.
            if (_moveOnClick || _moveOnMove) _cursor.Position = point;
        }

        public void LeftClick(PhysicalPoint point) { LeftClicks++; Land(point); }
        public void RightClick(PhysicalPoint point) { RightClicks++; Land(point); }
        public void MiddleClick(PhysicalPoint point) { MiddleClicks++; Land(point); }
        public void DoubleClick(PhysicalPoint point) { DoubleClicks++; Land(point); }
        public void MouseDown(PhysicalPoint point) => MouseDowns++;
        public void MouseUp(PhysicalPoint point) => MouseUps++;
        public void ReleaseLeftButton() { }
        public void Scroll(PhysicalPoint point, int deltaX, int deltaY) { }

        public void ScrollAt(PhysicalPoint point, int deltaX, int deltaY) => Land(point);

        public void ScrollAtAndRestore(PhysicalPoint at, PhysicalPoint restore, int deltaX, int deltaY)
        {
            // Net cursor position unchanged, exactly like the production atomic batch.
            _cursor.Position = restore;
        }

        public void SendKeyPress(ushort virtualKeyCode) => KeyPresses++;
        public void SendKeyDown(ushort virtualKeyCode) => KeyDowns++;
        public void SendKeyUp(ushort virtualKeyCode) => KeyUps++;
        public void SendText(string text) => Texts++;
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) => ModifiedKeyPresses++;

        public void SendKeyPress(ushort virtualKeyCode, bool isExtendedKey) => KeyPresses++;
        public void SendModifiedKeyPress(
            IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey) => ModifiedKeyPresses++;

        public void Scroll(PhysicalPoint point, int deltaX, int deltaY, IReadOnlySet<ModifierKey>? modifiers) =>
            ModifiedScrolls++;
    }

    private sealed class FakeIntegrity : IInputIntegrityProbe
    {
        public SelfTokenFacts ReadSelf() => new(0x2000, false, false);

        public ProcessTokenFacts InspectForeground() => ProcessTokenFacts.Unknown;

        public ProcessTokenFacts InspectPoint(PhysicalPoint point) => ProcessTokenFacts.Unknown;

        public ProcessTokenFacts InspectWindow(IntPtr hwnd) => ProcessTokenFacts.Unknown;
    }

    private sealed class FakeClock
    {
        public long NowMs { get; private set; }

        public void Advance(long milliseconds) => NowMs += milliseconds;
    }
}
