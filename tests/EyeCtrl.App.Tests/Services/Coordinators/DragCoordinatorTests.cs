namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// <b>The invariant: every MouseDown this coordinator issues is matched by a MouseUp, on every
/// exit path.</b>
/// <para>
/// A SendInput button-down is global OS state that outlives the process, so an unreleased drag
/// does not end when the app does — the successor starts in FreeLook, where the gaze engine moves
/// the cursor, and the user's gaze becomes a drag they cannot stop. The instinctive response to a
/// stuck drag is tray Exit or Settings → Restart, which is the very path that cements it.
/// </para>
/// <para>
/// This file previously held three tests, two of them about <c>Dispose</c> — asserting that it
/// does not throw. The seam, the fake and the test class all already existed; the assertion was
/// simply aimed at the wrong property, and <c>MouseUp</c> ended up asserted nowhere in the suite.
/// </para>
/// </summary>
public class DragCoordinatorTests
{
    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var c = new DragCoordinator();
        Check.ThatCode(() => c.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Mirrors the EventCoordinator contract test: a constructed-but-uninitialized
        // coordinator must still dispose cleanly (no NullRef on _context).
        new DragCoordinator().Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = new DragCoordinator();
        c.Dispose(); c.Dispose();
    }

    [Fact]
    public void SetupFailureAfterThePress_StillReleasesTheButton()
    {
        // The window between MouseDown and the cancel button existing. Here the overlay
        // construction fails (WPF refuses a Window off an STA thread), which is exactly the shape
        // the guard exists for: something between the press and the escape hatch throws. Before
        // the guard this escaped into the dispatcher, and the crash-to-die policy killed the
        // process with the button still pressed.
        var input = new TestFakes.FakeInputSimulator();
        var c = new DragCoordinator();
        c.Initialize(TestFakes.MakeDragContext(input));

        c.OnDragStarted(new PhysicalPoint(100, 100));

        Check.That(input.MouseDownCount).IsEqualTo(1);
        Check.That(input.ButtonsBalanced).IsTrue();
    }

    [Fact]
    public void SetupFailure_DoesNotEscapeIntoTheDispatcher()
    {
        // Sibling to the above and the reason it matters: the throw must be contained, not merely
        // compensated for. An escaping exception reaches DispatcherUnhandledException, which the
        // app deliberately leaves unhandled.
        var c = new DragCoordinator();
        c.Initialize(TestFakes.MakeDragContext(new TestFakes.FakeInputSimulator()));

        Check.ThatCode(() => c.OnDragStarted(new PhysicalPoint(10, 20))).DoesNotThrow();
    }

    [Fact]
    public void DisposeAfterAReleasedDrag_DoesNotReleaseTwice()
    {
        // Idempotence, which is what lets the invariant be stated as a count identity rather than
        // per-path bookkeeping: the release is owed once, no matter how many paths run.
        var input = new TestFakes.FakeInputSimulator();
        var c = new DragCoordinator();
        c.Initialize(TestFakes.MakeDragContext(input));

        c.OnDragStarted(new PhysicalPoint(5, 5));   // presses, fails setup, releases
        c.Dispose();                                 // must not release again

        Check.That(input.MouseDownCount).IsEqualTo(1);
        Check.That(input.MouseUpCount).IsEqualTo(1);
        Check.That(input.ButtonsBalanced).IsTrue();
    }

    [Fact]
    public void DisposeWithoutAnyDrag_TouchesTheButtonAtAll()
    {
        // The release must be conditional on this coordinator actually holding the button. An
        // unconditional MouseUp on dispose would release a button a caregiver's physical mouse is
        // legitimately holding — the same input-integrity failure in the opposite direction.
        var input = new TestFakes.FakeInputSimulator();
        var c = new DragCoordinator();
        c.Initialize(TestFakes.MakeDragContext(input));

        c.Dispose();

        Check.That(input.ButtonEvents).IsEmpty();
    }

    [Fact]
    public void DisposeRetriesAReleaseThatFailedEarlier()
    {
        // Covers the dispose release itself — the headline path, since tray Exit, Settings →
        // Restart and Windows logoff all reach Dispose and none of them released before.
        //
        // Reaching "the button is genuinely still held" is the hard part: every other test here
        // exercises the setup-failure path, where the abort has already released, so removing the
        // dispose release leaves them all green (verified by mutation — that is how this gap was
        // found). Driving a SUCCESSFUL setup would need a real EdgeButtonBarWindow, an Application
        // singleton and an exclusive STA collection.
        //
        // A failing MouseUp gets there headlessly, and tests a documented guarantee on the way:
        // the held flag is cleared only AFTER a successful release, so a release that throws stays
        // outstanding rather than being marked done. Dispose is the retry.
        var input = new ThrowOnFirstReleaseSimulator();
        var c = new DragCoordinator();
        c.Initialize(TestFakes.MakeDragContext(input));

        // Press, setup fails, abort attempts the release — and that release throws.
        Check.ThatCode(() => c.OnDragStarted(new PhysicalPoint(9, 9))).Throws<InvalidOperationException>();
        Check.That(input.MouseDownCount).IsEqualTo(1);
        Check.That(input.MouseUpCount).IsEqualTo(0);   // attempted, did not happen

        try { c.Dispose(); }
        catch (NullReferenceException) { /* harness has no OverlayManager; release precedes it */ }

        Check.That(input.MouseUpCount).IsEqualTo(1);
        Check.That(input.ButtonsBalanced).IsTrue();
    }

    [Fact]
    public void CancelReleasesBeforeTouchingTheOverlays()
    {
        // Ordering, not just presence. Cancel and drop used to hide the edge-button bar first,
        // putting two overlay dereferences ahead of the only step that touches OS-global state —
        // so a throw in EdgeButtonBar.Hide() skipped the release entirely and escaped into the
        // dispatcher, where the process dies with the button still pressed.
        //
        // The harness has no OverlayManager, so the overlay teardown throws here by construction.
        // That is the point: the release must already have happened by then. Asserting only
        // "balanced at the end" cannot see this — the ordering has to be observed through a throw.
        var input = new ThrowOnFirstReleaseSimulator();
        var c = new DragCoordinator();
        c.Initialize(TestFakes.MakeDragContext(input));

        // Leaves the button genuinely held: the abort's release throws and stays outstanding.
        Check.ThatCode(() => c.OnDragStarted(new PhysicalPoint(3, 3))).Throws<InvalidOperationException>();
        Check.That(input.MouseUpCount).IsEqualTo(0);

        try { c.OnDragCancel(); }
        catch (NullReferenceException) { /* the overlay hide — must come AFTER the release */ }

        Check.That(input.MouseUpCount).IsEqualTo(1);
        Check.That(input.ButtonsBalanced).IsTrue();
    }

    [Fact]
    public void DropReleasesBeforeTouchingTheOverlays()
    {
        // Same ordering guarantee on the drop path, which is the common one.
        var input = new ThrowOnFirstReleaseSimulator();
        var c = new DragCoordinator();
        c.Initialize(TestFakes.MakeDragContext(input));

        Check.ThatCode(() => c.OnDragStarted(new PhysicalPoint(4, 4))).Throws<InvalidOperationException>();

        try { c.OnDragDrop(new DragDropMessage(new PhysicalPoint(80, 80))); }
        catch (NullReferenceException) { /* the overlay hide — must come AFTER the release */ }

        Check.That(input.MouseUpCount).IsEqualTo(1);
        Check.That(input.ButtonsBalanced).IsTrue();
    }

    /// <summary>
    /// Fails the first release and succeeds afterwards. Private nested fake per repo convention
    /// (no mocking library).
    /// </summary>
    private sealed class ThrowOnFirstReleaseSimulator : IInputSimulator
    {
        private bool _thrown;

        public List<(string Action, PhysicalPoint Point)> ButtonEvents { get; } = new();

        public int MouseDownCount => ButtonEvents.Count(e => e.Action == "MouseDown");

        public int MouseUpCount => ButtonEvents.Count(e => e.Action == "MouseUp");

        public bool ButtonsBalanced => MouseDownCount == MouseUpCount;

        public void MouseDown(PhysicalPoint point) => ButtonEvents.Add(("MouseDown", point));

        public void MouseUp(PhysicalPoint point)
        {
            if (!_thrown)
            {
                _thrown = true;
                throw new InvalidOperationException("SendInput refused the release");
            }
            ButtonEvents.Add(("MouseUp", point));
        }

        public void MoveCursor(PhysicalPoint point) { }
        public void LeftClick(PhysicalPoint point) { }
        public void RightClick(PhysicalPoint point) { }
        public void DoubleClick(PhysicalPoint point) { }
        public void Scroll(PhysicalPoint point, int deltaX, int deltaY) { }
        public void SendKeyPress(ushort virtualKeyCode) { }
        public void SendKeyPress(ushort virtualKeyCode, bool isExtendedKey) { }
        public void SendKeyDown(ushort virtualKeyCode) { }
        public void SendKeyUp(ushort virtualKeyCode) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }

    [Fact]
    public void EveryOrderOfTeardown_LeavesTheButtonsBalanced()
    {
        // The generalised form. Drives the reachable exit paths in several orders and asserts only
        // the identity, so a path added later is covered without anyone remembering to extend a
        // per-path assertion.
        //
        // The overlay teardown inside cancel/drop throws here, because this harness has no
        // OverlayManager (IOverlayManager.EdgeButtonBar is typed as the concrete WPF window, so a
        // fake would need a real one). That is not an obstacle to the assertion — it IS the
        // assertion. Release now happens before any overlay work, so the button comes up even when
        // the cosmetic teardown fails; writing this test the other way round is what would hide a
        // regression. This exact throw caught the release sitting second on both paths.
        foreach (var teardown in new Action<DragCoordinator>[]
        {
            c => c.Dispose(),
            c => { c.OnDragCancel(); c.Dispose(); },
            c => { c.OnDragDrop(new DragDropMessage(new PhysicalPoint(7, 7))); c.Dispose(); },
            c => { c.Dispose(); c.Dispose(); },
        })
        {
            var input = new TestFakes.FakeInputSimulator();
            var c = new DragCoordinator();
            c.Initialize(TestFakes.MakeDragContext(input));
            c.OnDragStarted(new PhysicalPoint(42, 42));

            try { teardown(c); }
            catch (NullReferenceException) { /* harness has no OverlayManager; see above */ }

            Check.That(input.ButtonsBalanced).IsTrue();
        }
    }
}
