namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services.Coordinators;
using Xunit;

public class SettingsUiCoordinatorTests
{
    // The ctor needs a ModalInteractionState, a QuickActionsCoordinator, a
    // ToolbarLifecycleCoordinator, and a KeyboardCoordinator. None of these are touched by the
    // lifecycle contract tests below (they never call Initialize), so plain constructed instances
    // suffice — the same shape the sibling coordinator tests use.
    private static SettingsUiCoordinator NewCoordinator() =>
        new(new ModalInteractionState(),
            new QuickActionsCoordinator(),
            new ToolbarLifecycleCoordinator(new ModalInteractionState()),
            new KeyboardCoordinator(new ModalInteractionState(),
                new QuickActionsCoordinator(),
                new ToolbarLifecycleCoordinator(new ModalInteractionState())));

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
