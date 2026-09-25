namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Settings;

/// <summary>Spec D5 (auto-return), D14 (prediction selections return too), D15 (system
/// modifiers cleared on entry), M7 (modifier repaint after the swap). The trigger is a
/// NON-SPECIAL key activation — whatever modifiers are armed — or a prediction selection,
/// never while IsEmojiMode (emoji keys carry Output and flow through the same path while
/// emoji mode overlays the text sub-layout).</summary>
public class KeyboardAutoReturnTests
{
    private static KeyboardLayout LayoutWithAccents() => new()
    {
        Name = "T",
        DefaultSubLayout = "letters",
        SubLayouts =
        {
            ["letters"] = new SubLayout
            {
                Label = "ABC",
                Rows = new() { new() { new KeyDefinition { Label = "a", Output = "a" } } },
            },
            ["numbers"] = new SubLayout
            {
                Label = "123",
                Rows = new() { new() { new KeyDefinition { Label = "1", Output = "1" } } },
            },
            ["accents"] = new SubLayout
            {
                Label = "Acc",
                AutoReturn = true,
                Rows = new() { new() { new KeyDefinition { Label = "é", Output = "é", ShiftOutput = "É" } } },
            },
        },
    };

    private static KeyboardViewModel Vm()
        => new(new EyeCtrlSettings(), new WeakReferenceMessenger());

    private static KeyboardViewModel VmOnAccents()
    {
        var vm = Vm();
        vm.SetLayout(LayoutWithAccents());
        vm.SwitchSubLayout("accents");
        return vm;
    }

    [Fact]
    public void ActivateKey_OnAutoReturnSubLayout_ReturnsToDefault()
    {
        var vm = VmOnAccents();
        vm.ActivateKey("é", "É");
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters");
    }

    [Fact]
    public void TargetIsTheLayoutDefault_NotThePreviousSubLayout()
    {
        var vm = Vm();
        vm.SetLayout(LayoutWithAccents());
        vm.SwitchSubLayout("numbers");   // previous = numbers
        vm.SwitchSubLayout("accents");
        vm.ActivateKey("é", "É");
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters"); // not "numbers"
    }

    [Fact]
    public void SpecialKeys_DoNotTriggerReturn()
    {
        var vm = VmOnAccents();
        vm.HandleSpace();
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("accents");
        vm.HandleBackspace();
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("accents");
        vm.HandleTab();
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("accents");
    }

    [Fact]
    public void WhileInEmojiMode_AnEmojiOutput_DoesNotReturn()
    {
        var vm = VmOnAccents();
        vm.IsEmojiMode = true;
        vm.ActivateKey("😀", "😀");   // emoji keys carry Output through the same path
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("accents");
    }

    [Fact]
    public void PredictionSelection_OnAutoReturnSubLayout_ReturnsToDefault()
    {
        var vm = VmOnAccents();
        vm.HandlePredictionSelected("être");
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters");
    }

    [Fact]
    public void EnteringAutoReturnSubLayout_ClearsSystemModifiers_KeepsShiftAndCaps()
    {
        var vm = Vm();
        vm.SetLayout(LayoutWithAccents());
        vm.ToggleModifier(ModifierKey.Win);
        vm.ToggleModifier(ModifierKey.Ctrl);
        vm.ToggleModifier(ModifierKey.Shift);
        vm.ToggleModifier(ModifierKey.CapsLock);

        vm.SwitchSubLayout("accents");

        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Win)).IsFalse();
        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Ctrl)).IsFalse();
        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Shift)).IsTrue();
        Check.That(vm.ActiveModifiers.Contains(ModifierKey.CapsLock)).IsTrue();
        Check.That(vm.IsShifted).IsTrue();
    }

    [Fact]
    public void EnteringANonAutoReturnSubLayout_ClearsNothing()
    {
        var vm = Vm();
        vm.SetLayout(LayoutWithAccents());
        vm.ToggleModifier(ModifierKey.Win);

        vm.SwitchSubLayout("numbers");

        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Win)).IsTrue();
    }

    [Fact]
    public void ShiftOnAccents_TypesTheCapital_BeforeReturning()
    {
        var vm = VmOnAccents();
        vm.ToggleModifier(ModifierKey.Shift);
        vm.ActivateKey("é", "É");
        Check.That(vm.CurrentInput).IsEqualTo("É");   // D11: capitals via shiftOutput
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters");
    }

    [Fact]
    public void EverySwap_RaisesActiveModifiers_AFTERTheGridSwap()
    {
        // M7/H1: RebuildKeys draws static labels, so every swap must notify ActiveModifiers
        // AFTER the ActiveSubLayoutId change — the window's repaint must land on the NEW
        // grid. The ORDER is the requirement (a raise count cannot discriminate a
        // before-order bug — plan-r2), so the recorded sequence must alternate. The visible
        // outcome is pinned by the STA probe in Task 6.
        var vm = Vm();
        vm.SetLayout(LayoutWithAccents());

        var order = new List<string>();
        vm.PropertyChanged += (_, e) => order.Add(e.PropertyName ?? "");
        vm.SwitchSubLayout("numbers");   // NOT an autoReturn target — must still raise
        vm.SwitchSubLayout("letters");

        var filtered = order.Where(p => p
            is nameof(KeyboardViewModel.ActiveSubLayoutId)
            or nameof(KeyboardViewModel.ActiveModifiers)).ToList();
        Check.WithCustomMessage(
                "expected [ActiveSubLayoutId, ActiveModifiers, ActiveSubLayoutId, " +
                $"ActiveModifiers]; got [{string.Join(", ", filtered)}]")
            .That(filtered).ContainsExactly(
                nameof(KeyboardViewModel.ActiveSubLayoutId),
                nameof(KeyboardViewModel.ActiveModifiers),
                nameof(KeyboardViewModel.ActiveSubLayoutId),
                nameof(KeyboardViewModel.ActiveModifiers));
    }
}
