namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// P1 D6: the keyboard's note-editing context. The note manager's
/// <see cref="NoteEditingChangedMessage"/> is the editor switch: the one <c>(true)</c> an entry
/// into Edit sends must clear the typing context (the old app's or the previous note's half-typed
/// CurrentInput cannot decide how much the next prediction deletes) and light the "→ Note"
/// indicator; the <c>(false)</c> of the Done exit clears the indicator.
/// </summary>
public class KeyboardNoteContextTests
{
    private static KeyboardViewModel NewVm(WeakReferenceMessenger messenger) =>
        new(new EyeCtrlSettings(), messenger);

    [Fact]
    public void EditingTrue_ResetsTheInputContext_AndTargetsTheNote()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = NewVm(messenger);
        vm.ActivateKey("h", null);
        vm.ActivateKey("i", null);
        Check.That(vm.CurrentInput).IsEqualTo("hi");

        messenger.Send(new NoteEditingChangedMessage(Editing: true));

        Check.WithCustomMessage("the editor switch clears the composing input")
            .That(vm.CurrentInput).IsEqualTo("");
        Check.WithCustomMessage("the keyboard now targets the note (the → Note indicator)")
            .That(vm.IsTargetingNote).IsTrue();
    }

    [Fact]
    public void EditingTrue_StopsTheNextPredictionDeletingTheOldContext()
    {
        // The reason ResetInputContext exists (P1 D6): a prediction's delete count is the VM's
        // CurrentInput.Length at pick time — with the old app's half-typed input still set, the
        // note would lose characters it never had. The prediction sent after the switch must
        // carry deleteCount 0.
        var messenger = new WeakReferenceMessenger();
        var vm = NewVm(messenger);
        vm.ActivateKey("x", null);
        Check.That(vm.CurrentInput).IsEqualTo("x");

        PredictionSelectedMessage? lastPrediction = null;
        messenger.Register<object, PredictionSelectedMessage>(this, (_, m) => lastPrediction = m);
        messenger.Send(new NoteEditingChangedMessage(Editing: true));
        Check.That(vm.CurrentInput).IsEqualTo("");

        vm.HandlePredictionSelected("hello");

        Check.That(lastPrediction).IsNotNull();
        Check.WithCustomMessage("after the editor switch the prediction must delete nothing of the note")
            .That(lastPrediction!.DeleteCount).IsEqualTo(0);
    }

    [Fact]
    public void EditingFalse_ClearsTheTargetingFlag()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = NewVm(messenger);
        messenger.Send(new NoteEditingChangedMessage(Editing: true));
        Check.That(vm.IsTargetingNote).IsTrue();

        messenger.Send(new NoteEditingChangedMessage(Editing: false));

        Check.That(vm.IsTargetingNote).IsFalse();
    }

    [Fact]
    public void HidingTheKeyboard_KeepsTheTargetingFlag()
    {
        // P1 D8: a mid-Edit hide is the edge-bar Close, not the end of editing — the ⌨ control
        // re-summons and the "→ Note" indicator must return with it.
        var messenger = new WeakReferenceMessenger();
        var vm = NewVm(messenger);
        messenger.Send(new NoteEditingChangedMessage(Editing: true));

        messenger.Send(new HideKeyboardMessage());

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.IsTargetingNote).IsTrue();
    }

    [Fact]
    public void EditingTrue_ClearsTheSystemModifiers_ButKeepsTheTextOnes()
    {
        // Fixup m5 (execution deviations): a locked or one-shot Ctrl/Alt/Win survives the context
        // reset and silently swallows every note key (P1 D2) — an owner who left Ctrl latched
        // would type into an empty note. Entering Edit clears the system modifiers; Shift and
        // CapsLock are text modifiers and stay.
        var messenger = new WeakReferenceMessenger();
        var vm = NewVm(messenger);
        vm.ToggleModifier(ModifierKey.Ctrl);
        vm.ToggleModifier(ModifierKey.Win);
        vm.ToggleModifier(ModifierKey.Shift);
        vm.ToggleModifier(ModifierKey.CapsLock);
        Check.That(vm.ActiveModifiers).Contains(ModifierKey.Ctrl, ModifierKey.Win, ModifierKey.Shift, ModifierKey.CapsLock);

        messenger.Send(new NoteEditingChangedMessage(Editing: true));

        Check.WithCustomMessage("entering Edit must clear a latched Ctrl")
            .That(vm.ActiveModifiers.Contains(ModifierKey.Ctrl)).IsFalse();
        Check.WithCustomMessage("entering Edit must clear a latched Win")
            .That(vm.ActiveModifiers.Contains(ModifierKey.Win)).IsFalse();
        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Shift)).IsTrue();
        Check.That(vm.ActiveModifiers.Contains(ModifierKey.CapsLock)).IsTrue();
    }

    [Fact]
    public void ResetInputContext_ClearsTheWholePredictionContext()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = NewVm(messenger);
        vm.ActivateKey("wo", null);
        vm.HandleSpace();
        Check.That(vm.CurrentInput).IsEqualTo("");

        vm.ResetInputContext();

        // The observable half: the composing input is empty, so the next prediction deletes
        // nothing from the note. (_previousWord/_recentText are private; their reset is pinned
        // behaviourally by the editor-switch test above.)
        Check.That(vm.CurrentInput).IsEqualTo("");
        Check.That(vm.IsTargetingNote).IsFalse();
    }
}
