namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using Xunit;

public class KeyboardCoordinatorTests
{
    // The ctor needs a ModalInteractionState, a QuickActionsCoordinator, and a
    // ToolbarLifecycleCoordinator. None of these are touched by the lifecycle contract tests below
    // (they never call Initialize), so plain constructed instances suffice — the same shape the
    // sibling coordinator tests use.
    private static KeyboardCoordinator NewCoordinator() =>
        new(new ModalInteractionState(),
            new QuickActionsCoordinator(),
            new ToolbarLifecycleCoordinator(new ModalInteractionState()));

    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var c = NewCoordinator();
        Check.ThatCode(() => c.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Mirrors the EventCoordinator contract test: a constructed-but-uninitialized coordinator
        // must still dispose cleanly (no NullRef on _context). The PredictionStackFactory is
        // field-initialized, so its Dispose must run even when Initialize never did.
        NewCoordinator().Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = NewCoordinator();
        c.Dispose();
        c.Dispose();
    }

    [Fact]
    public void WordPredictor_ReturnsNull_BeforeLoadKeyboard()
    {
        // The 8-member public surface (re-exposed on EventCoordinator) includes WordPredictor and
        // Orchestrator; both must read null until LoadKeyboard builds a prediction stack.
        var c = NewCoordinator();
        Check.That(c.WordPredictor).IsNull();
        Check.That(c.Orchestrator).IsNull();
    }

    [Fact]
    public void Ctor_RejectsNullCollaborators()
    {
        var modal = new ModalInteractionState();
        var quickActions = new QuickActionsCoordinator();
        var toolbar = new ToolbarLifecycleCoordinator(modal);

        Check.ThatCode(() => new KeyboardCoordinator(null!, quickActions, toolbar)).Throws<ArgumentNullException>();
        Check.ThatCode(() => new KeyboardCoordinator(modal, null!, toolbar)).Throws<ArgumentNullException>();
        Check.ThatCode(() => new KeyboardCoordinator(modal, quickActions, null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void ApplyModalKeyboardState_FansOutToBothControllers_AndTheSharedFlag()
    {
        // The cross-cluster seam the decomposition introduced: showing the keyboard must suspend BOTH
        // the toolbar auto-hide and the quick-actions hot-edge summon, and publish the shared
        // ModalInteractionState flag that ToolbarLifecycleCoordinator's gaze-thread early-out reads.
        // Before the split these were three field writes in one method; now they cross three objects,
        // so a wiring mistake (e.g. a per-coordinator ModalInteractionState instead of the shared one)
        // would silently strand the flag. This drives the real seam, not a copy of it.
        var modal = new ModalInteractionState();

        var summon = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        var quickActions = new QuickActionsCoordinator();
        quickActions.InjectSummonForTesting(summon);

        var autoHide = new ToolbarAutoHideController(
            enabled: true, edge: "bottom", screenW: 1920, screenH: 1080,
            toolbarRect: new PhysicalRect(0, 1040, 1920, 40),
            hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
        var toolbar = new ToolbarLifecycleCoordinator(modal);
        toolbar.InjectAutoHideForTesting(autoHide);

        var keyboard = new KeyboardCoordinator(modal, quickActions, toolbar);

        keyboard.ApplyModalKeyboardState(true);
        Check.That(modal.KeyboardVisible).IsTrue();
        Check.That(toolbar.IsAutoHideSuspended).IsTrue();
        Check.That(quickActions.IsSummonSuspended).IsTrue();

        keyboard.ApplyModalKeyboardState(false);
        Check.That(modal.KeyboardVisible).IsFalse();
        Check.That(toolbar.IsAutoHideSuspended).IsFalse();
        Check.That(quickActions.IsSummonSuspended).IsFalse();
    }
}
