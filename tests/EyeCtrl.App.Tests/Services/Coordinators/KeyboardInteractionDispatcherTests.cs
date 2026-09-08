namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Keyboard;

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
}
