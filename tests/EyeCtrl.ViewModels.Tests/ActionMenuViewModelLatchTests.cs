namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// The action-menu modifier latch: a per-menu-session modifier set that toggles via
/// <see cref="ToggleActionMenuModifierMessage"/>, dims Drag/ScrollMode items as
/// non-dwell-targets on empty-to-non-empty crossings, is COPIED into
/// <see cref="ActionSelectedMessage"/> on capture, and resets on show (before the dim
/// read) and hide.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class ActionMenuViewModelLatchTests
{
    // Drag is on the DEFAULT secondary layer (PrimaryActions is LeftClick, RightClick,
    // DoubleClick, ScrollMode); ScrollMode is already primary. The settings pin all three
    // onto layer one so the dimming is tested where the owner meets it.
    private static EyeCtrlSettings SettingsWithDragOnLayerOne()
    {
        var settings = new EyeCtrlSettings();
        settings.ActionMenu.PrimaryActions = new List<InteractionAction>
        { InteractionAction.LeftClick, InteractionAction.Drag, InteractionAction.ScrollMode };
        return settings;
    }

    private static (ActionMenuViewModel Vm, WeakReferenceMessenger Messenger, List<ActionSelectedMessage> Sent) Build()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ActionMenuViewModel(SettingsWithDragOnLayerOne(), messenger);
        var sent = new List<ActionSelectedMessage>();
        messenger.Register<ActionSelectedMessage>(vm, (_, m) => sent.Add(m)); // vm keeps the recipient alive
        return (vm, messenger, sent);
    }

    private static ShowActionMenuMessage ShowMessage() => new(
        Center: new PhysicalPoint(500, 500),
        Actions: new List<InteractionAction>
        { InteractionAction.LeftClick, InteractionAction.Drag, InteractionAction.ScrollMode,
          InteractionAction.MiddleClick }, // secondary: the More target exists for the survival test
        CircleSize: 250, DwellTimeMs: 600, GazePoint: new PhysicalPoint(500, 500), ShowBack: false);
    // (Match the record's ACTUAL parameter list per InteractionMessages.cs — the construction
    // in ActionMenuViewModelLayerTests is the authoritative pattern.)

    [Fact]
    public void Toggle_LatchesAndUnlatches_RaisingLatchedModifiers()
    {
        var (vm, _, _) = Build();
        vm.Receive(ShowMessage());
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));

        Check.That(vm.LatchedModifiers.Contains(ModifierKey.Ctrl)).IsTrue();
        Check.That(vm.LastToggledModifier).IsEqualTo(ModifierKey.Ctrl);

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));
        Check.That(vm.LatchedModifiers.Contains(ModifierKey.Ctrl)).IsFalse();
    }

    [Fact]
    public void ToggleWhileHidden_IsANoOp()
    {
        var (vm, _, _) = Build();

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));

        Check.That(vm.LatchedModifiers).IsEmpty();
    }

    [Fact]
    public void ShowResetsTheLatch_BeforeTheDimRead_EvenWithoutAnInterveningHide()
    {
        var (vm, _, _) = Build();
        vm.Receive(ShowMessage());
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl)); // Drag+ScrollMode dimmed

        vm.Receive(ShowMessage()); // NO hide in between: the reset must happen before BuildLayer

        Check.That(vm.LatchedModifiers).IsEmpty();
        Check.That(vm.Items.First(i => i.Action == InteractionAction.Drag).IsDisabled).IsFalse();
        Check.That(vm.Items.First(i => i.Action == InteractionAction.ScrollMode).IsDisabled).IsFalse();
    }

    [Fact]
    public void Crossing_BumpsRingVersion_NonCrossingToggle_DoesNot()
    {
        var (vm, _, _) = Build();
        vm.Receive(ShowMessage());
        var before = vm.RingVersion;

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));
        Check.That(vm.RingVersion).IsEqualTo(before + 1); // empty → non-empty rebuild

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Shift));
        Check.That(vm.RingVersion).IsEqualTo(before + 1); // non-empty → non-empty: no rebuild

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Shift));
        Check.That(vm.RingVersion).IsEqualTo(before + 2); // back to empty: rebuild
    }

    [Fact]
    public void DimmedItems_AreNotDwellTargets_KeepSlots_AndNeverFire()
    {
        var (vm, messenger, sent) = Build();
        vm.Receive(ShowMessage());
        var dragBefore = vm.Items.First(i => i.Action == InteractionAction.Drag).Position;
        var scrollBefore = vm.Items.First(i => i.Action == InteractionAction.ScrollMode).Position;

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl)); // crossing: dim + rebuild

        var drag = vm.Items.First(i => i.Action == InteractionAction.Drag);
        var scrollMode = vm.Items.First(i => i.Action == InteractionAction.ScrollMode);
        Check.That(drag.IsDisabled).IsTrue();
        Check.That(scrollMode.IsDisabled).IsTrue();
        Check.That(drag.Position).IsEqualTo(dragBefore);       // slot geometry unchanged
        Check.That(scrollMode.Position).IsEqualTo(scrollBefore);

        // NOT a dwell target: a 100 ms-cadence full dwell at Drag's slot fires nothing AND
        // raises no progress (progress events only exist for registered targets). An
        // unconditional-AddTarget mutant fails both — the OnTargetActivated backstop alone
        // would still swallow a Drag activation, which is why the neighbour progress is
        // asserted too: the detector is alive and awarding dwell to LeftClick.
        for (var t = 0; t < 6; t++)
            vm.Receive(new GazeCursorMovedMessage(drag.Position, t * 100_000));
        // Mid-dwell progress pin: the unconditional-AddTarget mutant shows 5/6 here (the
        // detector awards dwell); the correct impl shows 0 (no target registered).
        Check.That(drag.DwellProgress).IsEqualTo(0);
        Check.That(sent.Where(m => m.Action == InteractionAction.Drag)).IsEmpty();

        var click = vm.Items.First(i => i.Action == InteractionAction.LeftClick);
        for (var t = 10; t < 20; t++)
            vm.Receive(new GazeCursorMovedMessage(click.Position, t * 100_000));
        // A full dwell on the neighbour fires it: the detector is alive and awarding dwell.
        Check.That(sent.Where(m => m.Action == InteractionAction.LeftClick)).Not.IsEmpty();
    }

    [Fact]
    public void Latch_SurvivesMoreAndBackToPrimary()
    {
        var (vm, _, _) = Build();
        vm.Receive(ShowMessage());
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));

        vm.ActivateForTest("More");
        Check.That(vm.LatchedModifiers.Contains(ModifierKey.Ctrl)).IsTrue();

        vm.ActivateForTest("BackToPrimary");
        Check.That(vm.LatchedModifiers.Contains(ModifierKey.Ctrl)).IsTrue();
    }

    [Fact]
    public void Capture_CopiesTheLatch_ToggleThenHideCannotEmptyTheMessage()
    {
        var (vm, messenger, sent) = Build();
        vm.Receive(ShowMessage());
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Shift));

        vm.ActivateForTest("LeftClick");

        Check.That(sent.Single().Modifiers!.Contains(ModifierKey.Shift)).IsTrue();

        vm.Receive(new HideActionMenuMessage());
        Check.That(sent.Single().Modifiers!.Contains(ModifierKey.Shift)).IsTrue(); // aliased set would be empty now
    }
}
