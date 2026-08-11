namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services.Coordinators;
using Xunit;

public class SettingsUiCoordinatorTests
{
    // The ctor needs a ModalInteractionState, a QuickActionsCoordinator, a
    // ToolbarLifecycleCoordinator, and a KeyboardCoordinator. None of these are touched by the
    // lifecycle contract tests below (they never call Initialize), so plain constructed instances
    // suffice — the same shape the sibling coordinator tests use. ONE shared ModalInteractionState
    // is threaded through every coordinator that takes one, matching the production wiring and
    // avoiding a trap for future flag-propagation tests (distinct instances would silently mask a
    // coordinator that failed to observe a sibling's flag flip).
    private static SettingsUiCoordinator NewCoordinator()
    {
        var modal = new ModalInteractionState();
        return new(modal,
            new QuickActionsCoordinator(),
            new ToolbarLifecycleCoordinator(modal),
            new KeyboardCoordinator(modal,
                new QuickActionsCoordinator(),
                new ToolbarLifecycleCoordinator(modal)));
    }

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
        // must still dispose cleanly (no NullRef on _context).
        NewCoordinator().Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = NewCoordinator();
        c.Dispose();
        c.Dispose();
    }
}
