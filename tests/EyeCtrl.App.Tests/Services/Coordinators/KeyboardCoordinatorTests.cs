namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services.Coordinators;
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
}
