namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using Xunit;

/// <summary>
/// N6 (fixup 03): the world's drain contains order-independence work without hiding failures. A
/// queued failure the test did not declare surfaces in that test; a declared one is consumed.
/// </summary>
public class ProbeTestWorldDrainTests
{
    [Fact]
    public void UndeclaredFailingQueuedWork_FailsTheDrain()
    {
        using var world = new ProbeTestWorld();
        world.Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("queued failure")));

        Check.WithCustomMessage("The drain swallowed an undeclared queued failure")
            .ThatCode(world.DrainQueuedWork).Throws<InvalidOperationException>();

        // The failing item was consumed; the next drain is clean, and so is Dispose.
        world.DrainQueuedWork();
    }

    [Fact]
    public void ADeclaredQueuedFailure_IsContained()
    {
        using var world = new ProbeTestWorld();
        world.ExpectQueuedFailure<NullReferenceException>();
        world.Dispatcher.BeginInvoke(new Action(() => throw new NullReferenceException("posted work")));

        Check.ThatCode(world.DrainQueuedWork).DoesNotThrow();
    }
}
