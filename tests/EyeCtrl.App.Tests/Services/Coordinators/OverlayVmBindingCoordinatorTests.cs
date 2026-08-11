namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services.Coordinators;
using Xunit;

public class OverlayVmBindingCoordinatorTests
{
    // The ctor needs a ModalInteractionState (the cursor-guard reads _modal.SettingsOpen). It is
    // never touched by the lifecycle contract tests below (they never call Initialize), so a plain
    // constructed instance suffices — the same shape the sibling coordinator tests use.
    private static OverlayVmBindingCoordinator NewCoordinator() =>
        new(new ModalInteractionState());

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

    [Fact]
    public void Ctor_RejectsNullModalState()
    {
        Check.ThatCode(() => new OverlayVmBindingCoordinator(null!)).Throws<ArgumentNullException>();
    }
}
