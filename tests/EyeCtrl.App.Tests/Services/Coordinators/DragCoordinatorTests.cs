namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
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
/// <para>
/// Two things shape the file after the drag-cancel flake fix. The trail overlay is behind
/// <c>DragCoordinator.CreateTrailOverlay</c>, so a successful drag runs on
/// <see cref="TestFakes.FakeDragTrailOverlay"/> — no STA thread, no window, nothing on the
/// desktop. And the drag's setup (overlay, anchor, cancel button) now runs BEFORE the press
/// (owner decision D1: "a drag that can't get ready does nothing"), so a setup failure issues no
/// button input at all and the genuinely-held states the release-ordering gates need are reached
/// through a successful start rather than through a compensated failure.
/// </para>
/// </summary>
public class DragCoordinatorTests
{
    private static DragCoordinator ThrowingOverlayFactory(
        DragCoordinator coordinator, Exception? failure = null)
    {
        var thrown = failure ?? new InvalidOperationException("the trail overlay refused to construct");
        coordinator.CreateTrailOverlay = () => throw thrown;
        return coordinator;
    }

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
    public void SetupFailureBeforeThePress_SendsNoButtonInput()
    {
        // F3, owner D1: a drag that cannot get ready does nothing. The overlay, the anchor and the
        // cancel button are all prepared BEFORE the press now, so this failure changes no OS button
        // state — the old order pressed first and released again at the grab origin, which is a
        // click the user never asked for. The explicit throwing factory replaces the old implicit
        // "WPF refuses a Window off an STA thread", so the failure is exactly the one under test.
        var input = new TestFakes.FakeInputSimulator();
        var (context, _) = TestFakes.MakeDragContextWithBar(input);
        var c = ThrowingOverlayFactory(new DragCoordinator());
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(100, 100));

        Check.That(c.LastSetupFailure).IsNotNull();
        Check.That(context.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(input.MouseDownCount).IsEqualTo(0);
        Check.That(input.MouseUpCount).IsEqualTo(0);
        Check.That(input.ButtonsBalanced).IsTrue();
    }

    [Fact]
    public void FatalExitRelease_AfterASetupFailure_SendsNothing()
    {
        // The fatal-exit release must be conditional on this coordinator actually holding the
        // button, like every other release path. After a setup failure no press was made, so it
        // owns nothing; a bare MouseUp here could release a caregiver's physically held button.
        var input = new TestFakes.FakeInputSimulator();
        var (context, _) = TestFakes.MakeDragContextWithBar(input);
        var c = ThrowingOverlayFactory(new DragCoordinator());
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(30, 40));   // setup fails before any press
        c.ReleaseHeldButtonForFatalExit();            // must be a no-op

        Check.That(input.MouseDownCount).IsEqualTo(0);
        Check.That(input.MouseUpCount).IsEqualTo(0);
        Check.That(input.ButtonsBalanced).IsTrue();
    }

    [Fact]
    public void SetupFailure_DoesNotEscapeIntoTheDispatcher()
    {
        // Sibling to the above and the reason it matters: the throw must be contained, not merely
        // compensated for. An escaping exception reaches DispatcherUnhandledException, which the
        // app deliberately leaves unhandled.
        var (context, _) = TestFakes.MakeDragContextWithBar(new TestFakes.FakeInputSimulator());
        var c = ThrowingOverlayFactory(new DragCoordinator());
        c.Initialize(context);

        Check.ThatCode(() => c.OnDragStarted(new PhysicalPoint(10, 20))).DoesNotThrow();
        Check.That(c.LastSetupFailure).IsNotNull();
    }

    [Fact]
    public void SetupFailure_RecordsTheThrownException()
    {
        // The flake's cause used to go only to the process-global Serilog logger, which a test
        // process leaves unconfigured — so CI reported "the state was FreeLook" and the exception
        // was unrecoverable. The SAME instance is recorded, so the next flake names its cause.
        var boom = new InvalidOperationException("the trail overlay refused to construct");
        var (context, _) = TestFakes.MakeDragContextWithBar(new TestFakes.FakeInputSimulator());
        var c = ThrowingOverlayFactory(new DragCoordinator(), boom);
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(10, 10));

        Check.That(c.LastSetupFailure).IsSameReferenceAs(boom);
    }

    [Fact]
    public void SuccessfulStart_LeavesLastSetupFailureNull()
    {
        var input = new TestFakes.FakeInputSimulator();
        var (context, _) = TestFakes.MakeDragContextWithBar(input);
        var c = new DragCoordinator { CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay() };
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(10, 10));

        Check.That(c.LastSetupFailure).IsNull();
    }

    [Fact]
    public void AFailedStartThenASuccessfulStart_ClearsLastSetupFailure()
    {
        // LastSetupFailure describes the LAST attempt, not "has anything ever failed": it is
        // cleared at the top of every start, before anything can fail.
        var (context, _) = TestFakes.MakeDragContextWithBar(new TestFakes.FakeInputSimulator());
        var overlay = new TestFakes.FakeDragTrailOverlay();
        var c = ThrowingOverlayFactory(new DragCoordinator());
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(10, 10));
        Check.That(c.LastSetupFailure).IsNotNull();

        c.CreateTrailOverlay = () => overlay;
        c.OnDragStarted(new PhysicalPoint(20, 20));

        Check.That(c.LastSetupFailure).IsNull();
        Check.That(overlay.ShowCount).IsEqualTo(1);
    }

    [Fact]
    public void SuccessfulStart_ShowsTheOverlayAndTheCancelButtonBeforePressing()
    {
        // F3's ordering, observed at the only instant that matters: FakeInputSimulator reads this
        // probe INSIDE MouseDown, so the values are what was true when the press was issued. Move
        // the press back before setup and the probe reports trailShown=False / cancelOnBar=False.
        var input = new TestFakes.FakeInputSimulator();
        var overlay = new TestFakes.FakeDragTrailOverlay();
        var (context, bar) = TestFakes.MakeDragContextWithBar(input);
        var c = new DragCoordinator { CreateTrailOverlay = () => overlay };
        c.Initialize(context);

        input.InjectionStateProbe =
            () => $"trailShown={overlay.ShowCount == 1};"
                + $"cancelOnBar={bar.ShownButtonIds.Contains("Drag_cancel")}";
        c.OnDragStarted(new PhysicalPoint(50, 60));

        Check.WithCustomMessage($"the press saw {input.InjectionState}.")
             .That(input.InjectionState).IsEqualTo("trailShown=True;cancelOnBar=True");
        Check.That(overlay.Calls[0]).IsEqualTo("Configure");
        Check.That(overlay.Calls[1]).IsEqualTo("Show");
        Check.That(bar.Calls[0]).IsEqualTo("ShowButtons");
        Check.That(input.MouseDownCount).IsEqualTo(1);
    }

    [Fact]
    public void TrailStartFailureAfterThePress_ReleasesTheButtonAndClosesTheOverlay()
    {
        // R1: the Send that starts the trail VM is the ONE step that runs after the press. It fans
        // out into DragTrailViewModel.Receive, whose property setters re-enter this coordinator's
        // PropertyChanged handler and call Update on the overlay — a WPF window plus DPI
        // arithmetic, so it can throw. Before this guard, that throw escaped OnDragStarted with the
        // button still down. The failure fires on the SECOND update so the VM is genuinely half
        // set up (IsDragActive true, SetupDwellTargets never reached); the reset has something to
        // undo, and its own assertion is discriminating.
        var input = new TestFakes.FakeInputSimulator();
        var overlay = new TestFakes.FakeDragTrailOverlay();
        var boom = new InvalidOperationException("the trail update refused to draw");
        overlay.UpdateFailure = boom;
        overlay.UpdateFailureOnCall = 2;
        var (context, bar) = TestFakes.MakeDragContextWithBar(input);
        var c = new DragCoordinator { CreateTrailOverlay = () => overlay };
        c.Initialize(context);

        Check.ThatCode(() => c.OnDragStarted(new PhysicalPoint(70, 80))).DoesNotThrow();

        Check.That(c.LastSetupFailure).IsSameReferenceAs(boom);
        Check.That(input.MouseDownCount).IsEqualTo(1);
        Check.That(input.MouseUpCount).IsEqualTo(1);
        Check.That(input.ButtonsBalanced).IsTrue();
        Check.That(context.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(overlay.CloseCount).IsEqualTo(1);
        Check.That(bar.Calls).Contains("Hide");
        Check.That(context.DragTrailVM.IsDragActive).IsFalse();
    }

    [Fact]
    public void ShowButtonsFailureAfterTheOverlayIsUp_ClosesTheOverlayAndHidesTheBar()
    {
        // R2 (survivor MK): every other setup failure throws from the factory, so the overlay does
        // not exist yet and the abort's CloseDragOverlay has nothing to close — deleting it stayed
        // green. Here the overlay exists and the bar refuses the buttons, so the abort must hide
        // the bar AND close the maximized trail window, with no button input at all.
        var input = new TestFakes.FakeInputSimulator();
        var overlay = new TestFakes.FakeDragTrailOverlay();
        var (context, bar) = TestFakes.MakeDragContextWithBar(input);
        bar.ShowButtonsFailure = new InvalidOperationException("the bar refused the buttons");
        var c = new DragCoordinator { CreateTrailOverlay = () => overlay };
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(11, 12));

        Check.That(c.LastSetupFailure).IsNotNull();
        Check.That(input.MouseDownCount).IsEqualTo(0);
        Check.That(input.MouseUpCount).IsEqualTo(0);
        Check.That(overlay.CloseCount).IsEqualTo(1);
        Check.That(bar.Calls).Contains("Hide");
        Check.That(context.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [StaFact]
    public void ProductionFactory_ReturnsTheRealOverlay()
    {
        // Construction only — no Show, so no HWND is created. The default must stay production's
        // real window: the seam exists so tests can substitute a fake, not so production can
        // silently lose its trail to a no-op.
        var c = new DragCoordinator();
        Check.That(c.CreateTrailOverlay()).IsInstanceOf<DragTrailOverlay>();
    }

    [Fact]
    public void DisposeAfterAFailedStart_DoesNotReleaseTwice()
    {
        // Idempotence, which is what lets the invariant be stated as a count identity rather than
        // per-path bookkeeping: the release is owed once, no matter how many paths run. After a
        // setup failure nothing is owed at all — there was no press — so dispose must not invent one.
        var input = new TestFakes.FakeInputSimulator();
        var (context, _) = TestFakes.MakeDragContextWithBar(input);
        var c = ThrowingOverlayFactory(new DragCoordinator());
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(5, 5));   // setup fails before any press
        c.Dispose();                                 // must not touch the button

        Check.That(input.MouseDownCount).IsEqualTo(0);
        Check.That(input.MouseUpCount).IsEqualTo(0);
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
        // A failing MouseUp gets to a genuinely-held button headlessly: the drag's setup now
        // SUCCEEDS over the fake overlay and the fake bar, and the first release attempt — the
        // fatal-exit one — throws. A documented guarantee is tested on the way: the held flag is
        // cleared only AFTER a successful release, so a release that throws stays outstanding
        // rather than being marked done. Dispose is the retry.
        var input = new ThrowOnFirstReleaseSimulator();
        var (context, _) = TestFakes.MakeDragContextWithBar(input);
        var c = new DragCoordinator { CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay() };
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(9, 9));   // setup succeeds: the button is genuinely held
        Check.That(input.MouseDownCount).IsEqualTo(1);
        Check.That(input.MouseUpCount).IsEqualTo(0);

        Check.ThatCode(() => c.ReleaseHeldButtonForFatalExit()).Throws<InvalidOperationException>();
        Check.That(input.MouseUpCount).IsEqualTo(0);   // attempted, did not happen

        c.Dispose();

        Check.That(input.MouseUpCount).IsEqualTo(1);
        Check.That(input.ButtonsBalanced).IsTrue();
    }

    [Fact]
    public void FatalExitRelease_DuringALiveDrag_SendsTheMouseUp()
    {
        // The POSITIVE case for the fatal-exit path (review finding #9: only the negative,
        // not-held case was covered). "Genuinely held" is now reached headlessly: the fake overlay
        // and the fake bar let the setup SUCCEED, so the button is down with no abort having
        // released it. This is the state a fatal exception elsewhere in the process must not
        // strand — the press outlives the process.
        var input = new TestFakes.FakeInputSimulator();
        var (context, bar) = TestFakes.MakeDragContextWithBar(input);
        var c = new DragCoordinator { CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay() };
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(300, 300));

        Check.That(bar.ShownButtonIds).ContainsExactly("Drag_cancel"); // setup succeeded — no abort
        Check.That(input.MouseDownCount).IsEqualTo(1);
        Check.That(input.MouseUpCount).IsEqualTo(0);                   // genuinely held

        c.ReleaseHeldButtonForFatalExit();

        Check.That(input.MouseUpCount).IsEqualTo(1);
        Check.That(input.ButtonsBalanced).IsTrue();
    }

    [Fact]
    public void DisposeDuringALiveDrag_ReleasesTheButton()
    {
        // THE headline path, and until IOverlayManager.EdgeButtonBar was narrowed to an interface
        // it could not be tested at all. Tray Exit, Settings → Restart and Windows logoff all reach
        // Dispose with a drag genuinely in progress, and none of them released — so the successor
        // process came up in FreeLook with the physical button still down and the user's gaze
        // became a drag they could not stop.
        //
        // "Genuinely in progress" is the hard part: it requires OnDragStarted's setup to SUCCEED,
        // which calls ShowButtons. The fake bar made that possible; the fake trail overlay (the
        // CreateTrailOverlay seam) keeps the last window out of it, so this is a plain [Fact].
        var input = new TestFakes.FakeInputSimulator();
        var (context, bar) = TestFakes.MakeDragContextWithBar(input);
        var c = new DragCoordinator { CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay() };
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(300, 300));

        // Setup completed: the cancel button is up, so the user had a way out and the abort path
        // did NOT run. Without these two the test could pass on the failure path by accident.
        Check.That(bar.ShownButtonIds).ContainsExactly("Drag_cancel");
        Check.That(input.MouseDownCount).IsEqualTo(1);
        Check.That(input.MouseUpCount).IsEqualTo(0);   // genuinely held

        c.Dispose();

        Check.That(input.MouseUpCount).IsEqualTo(1);
        Check.That(input.ButtonsBalanced).IsTrue();
    }

    [Fact]
    public void ASuccessfulDragThenDrop_BalancesAndClearsTheBar()
    {
        // The ordinary path, now observable end to end. Also pins that the drop clears the cancel
        // button and closes the trail: leaving either up after the drag ends would dwell-activate
        // into a cancel for a drag that no longer exists, or paint over the desktop indefinitely.
        var input = new TestFakes.FakeInputSimulator();
        var overlay = new TestFakes.FakeDragTrailOverlay();
        var (context, bar) = TestFakes.MakeDragContextWithBar(input);
        var c = new DragCoordinator { CreateTrailOverlay = () => overlay };
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(10, 10));
        c.OnDragDrop(new DragDropMessage(new PhysicalPoint(90, 90)));

        Check.That(input.ButtonsBalanced).IsTrue();
        Check.That(bar.ShownButtonIds).IsEmpty();
        Check.That(bar.Calls).Contains("Hide");
        Check.That(overlay.CloseCount).IsEqualTo(1);
    }

    [Fact]
    public void ASuccessfulDragThenCancel_ReleasesAtTheGrabOrigin()
    {
        // Cancel releases where the drag STARTED, not where gaze happens to be — releasing at a
        // stale or arbitrary point drops the dragged object somewhere the user never chose.
        var input = new TestFakes.FakeInputSimulator();
        var (context, _) = TestFakes.MakeDragContextWithBar(input);
        var c = new DragCoordinator { CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay() };
        c.Initialize(context);

        var origin = new PhysicalPoint(123, 456);
        c.OnDragStarted(origin);
        c.OnDragCancel();

        Check.That(input.ButtonsBalanced).IsTrue();
        var release = input.ButtonEvents.Single(e => e.Action == "MouseUp");
        Check.That(release.Point.X).IsEqualTo(origin.X);
        Check.That(release.Point.Y).IsEqualTo(origin.Y);
    }

    [Fact]
    public void CancelReleasesBeforeTouchingTheOverlays()
    {
        // Ordering, not just presence. Cancel and drop used to hide the edge-button bar first,
        // putting two overlay dereferences ahead of the only step that touches OS-global state —
        // so a throw in EdgeButtonBar.Hide() skipped the release entirely and escaped into the
        // dispatcher, where the process dies with the button still pressed.
        //
        // The bar is made to throw from Hide, and the drag is genuinely held through a successful
        // start, so this observes the throw AFTER the release. Asserting only "balanced at the end"
        // cannot see the ordering.
        var input = new TestFakes.FakeInputSimulator();
        var (context, bar) = TestFakes.MakeDragContextWithBar(input);
        var c = new DragCoordinator { CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay() };
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(3, 3));
        Check.That(input.MouseDownCount).IsEqualTo(1);
        Check.That(input.MouseUpCount).IsEqualTo(0);   // genuinely held

        bar.HideFailure = new InvalidOperationException("the bar refused the hide");
        try { c.OnDragCancel(); }
        catch (InvalidOperationException) { /* the overlay teardown — must come AFTER the release */ }

        Check.That(input.MouseUpCount).IsEqualTo(1);
        Check.That(input.ButtonsBalanced).IsTrue();
    }

    [Fact]
    public void DropReleasesBeforeTouchingTheOverlays()
    {
        // Same ordering guarantee on the drop path, which is the common one.
        var input = new TestFakes.FakeInputSimulator();
        var (context, bar) = TestFakes.MakeDragContextWithBar(input);
        var c = new DragCoordinator { CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay() };
        c.Initialize(context);

        c.OnDragStarted(new PhysicalPoint(4, 4));
        Check.That(input.MouseDownCount).IsEqualTo(1);

        bar.HideFailure = new InvalidOperationException("the bar refused the hide");
        try { c.OnDragDrop(new DragDropMessage(new PhysicalPoint(80, 80))); }
        catch (InvalidOperationException) { /* the overlay teardown — must come AFTER the release */ }

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
        // The generalised form. Drives the reachable exit paths in several orders against a drag
        // that genuinely started — the fake overlay and the fake bar let setup succeed — and
        // asserts the identity, so a path added later is covered without anyone remembering to
        // extend a per-path assertion. Exactly ONE MouseUp is asserted rather than balance alone:
        // the last case drops (releasing once) and then runs the fatal-exit release on a button
        // that is no longer held, which must be a no-op rather than a second release of whatever
        // the caregiver's physical mouse may now be holding.
        foreach (var teardown in new Action<DragCoordinator>[]
        {
            c => c.Dispose(),
            c => { c.OnDragCancel(); c.Dispose(); },
            c => { c.OnDragDrop(new DragDropMessage(new PhysicalPoint(7, 7))); c.Dispose(); },
            c => { c.Dispose(); c.Dispose(); },
            c => { c.OnDragDrop(new DragDropMessage(new PhysicalPoint(7, 7))); c.ReleaseHeldButtonForFatalExit(); },
        })
        {
            var input = new TestFakes.FakeInputSimulator();
            var (context, _) = TestFakes.MakeDragContextWithBar(input);
            var c = new DragCoordinator { CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay() };
            c.Initialize(context);
            c.OnDragStarted(new PhysicalPoint(42, 42));
            Check.That(input.MouseDownCount).IsEqualTo(1);

            teardown(c);

            Check.That(input.MouseUpCount).IsEqualTo(1);
            Check.That(input.ButtonsBalanced).IsTrue();
        }
    }
}
