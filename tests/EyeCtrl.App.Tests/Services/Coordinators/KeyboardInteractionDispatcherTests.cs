namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Lifecycle contract tests for <see cref="KeyboardInteractionDispatcher"/>. The dispatcher's
/// Group-B handlers (key activation / prediction / action requested) need fuller context to
/// drive directly, so the per-coordinator minimum is the lifecycle contract — Initialize
/// null-arg, Dispose-before-init, Dispose-idempotent — mirroring the sibling coordinators
/// (<see cref="DragCoordinatorTests"/>). <see cref="HandleSpecialKey"/> is the exception: it
/// touches only the input simulator, so it is driven directly like
/// <c>QuickActionDispatcherTests.ExecuteKeyboardShortcut</c>.
/// </summary>
public class KeyboardInteractionDispatcherTests
{
    /// <summary>
    /// The construction pattern the modifier-routing tests need: a fake simulator wired through
    /// <see cref="TestFakes.MakeContext(IInputSimulator)"/> with <c>TargetHighlightVM</c> supplied
    /// (the handler reads it unconditionally) and the parameterless dispatcher ctor +
    /// <c>Initialize(ctx)</c>. Messages sent through the returned dispatcher must carry
    /// <c>IsPreciseTarget: true</c> so <c>HandleActionRequested</c> never reads
    /// <c>Context.GazeEngine.StableCentroid</c> — MakeContext leaves GazeEngine null!.
    /// </summary>
    private static (KeyboardInteractionDispatcher D, TestFakes.FakeInputSimulator Sim) BuildWithFakeInput()
    {
        var input = new TestFakes.FakeInputSimulator();
        var ctx = TestFakes.MakeContext(input) with
            { TargetHighlightVM = new TargetHighlightOverlayViewModel() };
        var dispatcher = new KeyboardInteractionDispatcher();
        dispatcher.Initialize(ctx);
        return (dispatcher, input);
    }

    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var c = new KeyboardInteractionDispatcher();
        Check.ThatCode(() => c.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Mirrors the EventCoordinator contract test: a constructed-but-uninitialized
        // coordinator must still dispose cleanly (no NullRef on _context).
        new KeyboardInteractionDispatcher().Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = new KeyboardInteractionDispatcher();
        c.Dispose(); c.Dispose();
    }

    [Fact]
    public void HandleSpecialKey_TextControlKey_WithModifiers_SendsModifiedPress()
    {
        // Backspace had no arm in the inline switch this replaced — the "shortcut:" layout keys
        // (Ctrl+Backspace word delete among them) need it to resolve through KeyNameMapper.
        var input = new TestFakes.FakeInputSimulator();
        var d = new KeyboardInteractionDispatcher();
        d.Initialize(TestFakes.MakeContext(input));

        d.HandleSpecialKey("Backspace", new HashSet<ModifierKey> { ModifierKey.Ctrl });

        Check.That(input.LastModifiedKeyPress).IsNotNull();
        Check.That(input.LastModifiedKeyPress!.VirtualKeyCode).IsEqualTo((ushort)0x08);
        Check.That(input.LastModifiedKeyPress.Modifiers).Contains(ModifierKey.Ctrl);
    }

    [Fact]
    public void HandleSpecialKey_ExtendedKey_SendsWithExtendedFlag()
    {
        var input = new TestFakes.FakeInputSimulator();
        var d = new KeyboardInteractionDispatcher();
        d.Initialize(TestFakes.MakeContext(input));

        d.HandleSpecialKey("Insert", null);

        Check.That(input.LastKeyPress).IsNotNull();
        var (vk, extended) = input.LastKeyPress!.Value;
        Check.That(vk).IsEqualTo((ushort)0x2D);
        Check.That(extended).IsTrue();
    }

    [Fact]
    public void HandleSpecialKey_UnknownName_SendsNothing()
    {
        var input = new TestFakes.FakeInputSimulator();
        var d = new KeyboardInteractionDispatcher();
        d.Initialize(TestFakes.MakeContext(input));

        d.HandleSpecialKey("NotAKey", null);

        Check.That(input.LastModifiedKeyPress).IsNull();
        Check.That(input.LastKeyPress).IsNull();
    }

    [Fact]
    public void HandleActionRequested_MiddleClick_DispatchesToTheSimulator()
    {
        var input = new TestFakes.FakeInputSimulator();
        // TargetHighlightVM is read unconditionally at the end of the handler, so a bare
        // MakeContext (null!) would NRE before reaching the simulator. IsPreciseTarget keeps
        // the handler off GazeEngine, which MakeContext also leaves null!.
        var ctx = TestFakes.MakeContext(input) with
        {
            TargetHighlightVM = new TargetHighlightOverlayViewModel()
        };
        var d = new KeyboardInteractionDispatcher();
        d.Initialize(ctx);

        d.HandleActionRequested(new ActionRequestedMessage(
            InteractionAction.MiddleClick, new PhysicalPoint(300, 400), IsPreciseTarget: true));

        Check.That(input.LastMiddleClick).IsNotNull();
        var clicked = input.LastMiddleClick!.Value;
        Check.That(clicked.X).IsEqualTo(300);
        Check.That(clicked.Y).IsEqualTo(400);
    }

    // ---- The ring's Keyboard action (external review I1) ------------------------------------
    // The KeyboardShowRequestedMessage send is Feature A's only production sender; until now it
    // appeared in no test, so deleting the intercept left the whole suite green (the mutation
    // that motivated these facts). HandleActionRequested IS headless-reachable for this action:
    // IsPreciseTarget bypasses the GazeEngine read, the switch has no Keyboard case, and a
    // hidden TargetHighlightVM skips the overlay cleanup — only Messenger and the VM are touched.

    [Fact]
    public void HandleActionRequested_Keyboard_SendsShowMessage_AndNothingElse()
    {
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator());
        ctx = ctx with { TargetHighlightVM = new TargetHighlightOverlayViewModel() };
        var dispatcher = new KeyboardInteractionDispatcher();
        dispatcher.Initialize(ctx);

        var showCount = 0;
        ctx.Messenger.Register<object, KeyboardShowRequestedMessage>(this, (_, _) => showCount++);

        dispatcher.HandleActionRequested(new ActionRequestedMessage(
            InteractionAction.Keyboard, new PhysicalPoint(500, 500), IsPreciseTarget: true));

        Check.That(showCount).IsEqualTo(1);
    }

    [Fact]
    public void HandleActionRequested_ClickActions_NeverSendShowMessage()
    {
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator());
        ctx = ctx with { TargetHighlightVM = new TargetHighlightOverlayViewModel() };
        var dispatcher = new KeyboardInteractionDispatcher();
        dispatcher.Initialize(ctx);

        var showCount = 0;
        ctx.Messenger.Register<object, KeyboardShowRequestedMessage>(this, (_, _) => showCount++);

        dispatcher.HandleActionRequested(new ActionRequestedMessage(
            InteractionAction.LeftClick, new PhysicalPoint(500, 500), IsPreciseTarget: true));
        dispatcher.HandleActionRequested(new ActionRequestedMessage(
            InteractionAction.ScrollUp, new PhysicalPoint(500, 500), IsPreciseTarget: true));

        Check.That(showCount).IsEqualTo(0);
    }

    // ---- Modifier routing (action-menu modifier keys, Task 3) -------------------------------
    // Every message carries IsPreciseTarget: true so HandleActionRequested never reads
    // Context.GazeEngine.StableCentroid (MakeContext leaves GazeEngine null!).

    [Fact]
    public void ModifiedLeftClick_RoutesToTheModifiedOverload()
    {
        var (dispatcher, simulator) = BuildWithFakeInput();
        var mods = new HashSet<ModifierKey> { ModifierKey.Shift };

        dispatcher.HandleActionRequested(new ActionRequestedMessage(
            InteractionAction.LeftClick, new PhysicalPoint(10, 10), IsPreciseTarget: true, Modifiers: mods));

        Check.That(simulator.ModifiedClicks).ContainsExactly(
            (InteractionAction.LeftClick, new PhysicalPoint(10, 10), mods));
        Check.That(simulator.BareClickCount).IsEqualTo(0);
    }

    [Fact]
    public void EmptySetMessage_NeverTouchesTheModifiedOverload()
    {
        var (dispatcher, simulator) = BuildWithFakeInput();

        // The VM sends null when the latch is empty — a mods != null gating mutant would route
        // through the modified overload. Same observable behaviour, but the null path is the
        // contract: the modified overload must not be reachable with an empty set.
        dispatcher.HandleActionRequested(new ActionRequestedMessage(
            InteractionAction.LeftClick, new PhysicalPoint(10, 10), IsPreciseTarget: true, Modifiers: new HashSet<ModifierKey>()));

        Check.That(simulator.ModifiedClicks).IsEmpty();
        Check.That(simulator.BareClickCount).IsEqualTo(1);
    }

    [Fact]
    public void BareLeftClick_NeverTouchesTheModifiedOverload()
    {
        var (dispatcher, simulator) = BuildWithFakeInput();

        dispatcher.HandleActionRequested(new ActionRequestedMessage(
            InteractionAction.LeftClick, new PhysicalPoint(10, 10), IsPreciseTarget: true));

        Check.That(simulator.ModifiedClicks).IsEmpty();
        Check.That(simulator.BareClickCount).IsEqualTo(1);
    }

    [Fact]
    public void AllFourModifiedClickArms_RouteTheirAction()
    {
        var (dispatcher, simulator) = BuildWithFakeInput();
        var mods = new HashSet<ModifierKey> { ModifierKey.Ctrl };

        dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.LeftClick, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
        dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.RightClick, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
        dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.MiddleClick, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
        dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.DoubleClick, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));

        Check.That(simulator.ModifiedClicks.Select(c => c.Action)).ContainsExactly(
            InteractionAction.LeftClick, InteractionAction.RightClick,
            InteractionAction.MiddleClick, InteractionAction.DoubleClick);
    }

    [Fact]
    public void ModifiedScroll_SendsOneNotch_SignCorrectPerArm()
    {
        var (dispatcher, simulator) = BuildWithFakeInput();
        var mods = new HashSet<ModifierKey> { ModifierKey.Ctrl };

        dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollUp, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
        dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollDown, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
        dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollLeft, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
        dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollRight, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));

        Check.That(simulator.ModifiedScrolls).ContainsExactly(
            ((PhysicalPoint Point, int Dx, int Dy))(new PhysicalPoint(0, 0), 0, 1),
            (new PhysicalPoint(0, 0), 0, -1),
            (new PhysicalPoint(0, 0), -1, 0),
            (new PhysicalPoint(0, 0), 1, 0)); // point included per external review M3
    }

    [Fact]
    public void UnmodifiedScroll_KeepsThreeNotches()
    {
        var (dispatcher, simulator) = BuildWithFakeInput();

        dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollUp, new PhysicalPoint(0, 0), IsPreciseTarget: true));

        Check.That(simulator.BareScrolls).ContainsExactly(((int Dx, int Dy))(0, 3));
    }
}
