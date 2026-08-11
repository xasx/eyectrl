namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services.Coordinators;

/// <summary>
/// Lifecycle contract tests for <see cref="KeyboardInteractionDispatcher"/>. The dispatcher's
/// Group-B handlers (key activation / prediction / special key / action requested) need fuller
/// context to drive directly, so the per-coordinator minimum is the lifecycle contract —
/// Initialize null-arg, Dispose-before-init, Dispose-idempotent — mirroring the sibling
/// coordinators (<see cref="DragCoordinatorTests"/>).
/// </summary>
public class KeyboardInteractionDispatcherTests
{
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
}
