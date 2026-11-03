namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;

/// <summary>
/// C10 fixup-01 F3: the gate's episode lifecycle. An unblocked wait is complete (a caller may use
/// it unconditionally); a block must hand out a FRESH incomplete wait, or a deferral loop spins on
/// the previous, already-completed task and starves the dispatcher instead of awaiting the clear.
/// </summary>
public class BlockedInputGateTests
{
    [Fact]
    public void ABreakDueWhileUnblocked_IsAlreadyClear()
    {
        var gate = new BlockedInputGate();

        Check.That(gate.IsBlocked).IsFalse();
        Check.That(gate.WaitForClearAsync().IsCompleted).IsTrue();
    }

    [Fact]
    public void ANewBlock_StartsAFreshUnclearedWait()
    {
        var gate = new BlockedInputGate();

        gate.Set(true);

        Check.That(gate.IsBlocked).IsTrue();
        Check.WithCustomMessage("the block reused the already-completed unblocked wait: the loop spins.")
            .That(gate.WaitForClearAsync().IsCompleted).IsFalse();
    }

    [Fact]
    public void TheClear_CompletesTheWaitThatWasRegisteredDuringTheBlock()
    {
        var gate = new BlockedInputGate();
        gate.Set(true);
        var wait = gate.WaitForClearAsync();

        gate.Set(false);

        Check.That(wait.IsCompleted).IsTrue();
        Check.That(gate.WaitForClearAsync().IsCompleted).IsTrue();
    }

    [Fact]
    public void ASecondBlock_NeedsItsOwnClear()
    {
        var gate = new BlockedInputGate();
        gate.Set(true);
        gate.Set(false);

        gate.Set(true);
        var second = gate.WaitForClearAsync();
        Check.That(second.IsCompleted).IsFalse();

        gate.Set(false);
        Check.That(second.IsCompleted).IsTrue();
    }

    [Fact]
    public void SettingTheSameValueTwice_DoesNotSwapTheWait()
    {
        var gate = new BlockedInputGate();
        gate.Set(true);
        var wait = gate.WaitForClearAsync();

        gate.Set(true);

        Check.That(gate.WaitForClearAsync()).IsSameReferenceAs(wait);
    }
}
