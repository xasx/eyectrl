namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services.Coordinators;
using Xunit;

public class OverlayMessageCoordinatorTests
{
    // The ctor needs a ToolbarLifecycleCoordinator (calibration SetActive/ReassertVisibility seams)
    // and the VmBinding sibling (the HideBackdrop seam). None are touched by the lifecycle contract
    // tests below (they never call Initialize), so plain constructed instances suffice — the same
    // shape the sibling coordinator tests use.
    private static OverlayMessageCoordinator NewCoordinator()
    {
        var modal = new ModalInteractionState();
        var toolbar = new ToolbarLifecycleCoordinator(modal);
        var vmBinding = new OverlayVmBindingCoordinator(modal);
        return new OverlayMessageCoordinator(toolbar, vmBinding);
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
