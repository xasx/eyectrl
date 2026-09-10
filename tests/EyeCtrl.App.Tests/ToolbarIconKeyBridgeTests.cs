namespace EyeCtrl.App.Tests;

using System;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// ToolbarViewModel names its icons as strings because EyeCtrl.ViewModels must not reference the
/// WPF Controls assembly. That string is parsed back to a <see cref="ToolbarIconKey"/> when the
/// toolbar builds its buttons, so a typo is a runtime throw during window construction — in front
/// of a user whose only input channel is an eye tracker. This project sees both sides; it is the
/// only place the two ends can be pinned together.
///
/// A key that merely names a real <see cref="ToolbarIconKey"/> member is not enough: swapping
/// <see cref="SelectionModeIcons.ManualSelection"/> and <see cref="SelectionModeIcons.AutoSelection"/>'s
/// values would still pass a validity-only check, and would still show the wrong icon for two of
/// the three modes. <see cref="EachMode_MapsToItsIntendedIcon"/> and
/// <see cref="PauseButton_SwapsBetweenPauseAndPlay"/> assert the actual mapped value, not just its
/// validity.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ToolbarIconKeyBridgeTests
{
    [Theory]
    [InlineData(SelectionMode.None, ToolbarIconKey.SelectionNone)]
    [InlineData(SelectionMode.ManualSelection, ToolbarIconKey.SelectionManual)]
    [InlineData(SelectionMode.AutoSelection, ToolbarIconKey.SelectionAuto)]
    public void EachMode_MapsToItsIntendedIcon(SelectionMode mode, ToolbarIconKey expected)
    {
        var key = SelectionModeIcons.ForMode(mode);
        Check.WithCustomMessage($"SelectionModeIcons.ForMode({mode}) = \"{key}\", expected it to "
                + $"name {expected}.")
            .That(Enum.Parse<ToolbarIconKey>(key)).IsEqualTo(expected);
    }

    [Theory]
    [InlineData(InteractionAction.LeftClick, ToolbarIconKey.ModeLeftClick)]
    [InlineData(InteractionAction.RightClick, ToolbarIconKey.ModeRightClick)]
    [InlineData(InteractionAction.DoubleClick, ToolbarIconKey.ModeDoubleClick)]
    [InlineData(InteractionAction.MiddleClick, ToolbarIconKey.ModeMiddleClick)]
    public void EachLockedAction_MapsToItsIntendedModeIcon(InteractionAction action, ToolbarIconKey expected)
    {
        var key = EyeCtrl.ViewModels.ModesIcons.ForAction(action);
        Check.WithCustomMessage($"ModesIcons.ForAction({action}) = \"{key}\", expected it to name {expected}.")
            .That(Enum.Parse<ToolbarIconKey>(key)).IsEqualTo(expected);
    }

    /// <summary>
    /// The Pause button's icon key is written after construction (<c>ToolbarViewModel.Receive
    /// (StateChangedMessage)</c>), so the constructor-only totality check below never sees it.
    /// This is the one place that value is pinned against the real enum.
    /// </summary>
    [Fact]
    public void PauseButton_SwapsBetweenPauseAndPlay()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), messenger);
        var pauseButton = vm.Buttons.Single(b => b.Id == "Pause");

        messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Check.That(Enum.Parse<ToolbarIconKey>(pauseButton.IconKey)).IsEqualTo(ToolbarIconKey.Play);

        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(Enum.Parse<ToolbarIconKey>(pauseButton.IconKey)).IsEqualTo(ToolbarIconKey.Pause);
    }

    /// <summary>
    /// Totality check over every button's constructor-time key. Uses <see cref="Enum.GetNames"/>
    /// membership rather than <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> — TryParse also
    /// accepts numeric text ("7") and comma-separated flag lists, so it does not strictly prove
    /// "names a member" the way this guard needs.
    /// </summary>
    [Fact]
    public void EveryViewModelButtonIconKey_NamesARealToolbarIconKey()
    {
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var memberNames = Enum.GetNames<ToolbarIconKey>();

        foreach (var button in vm.Buttons)
            Check.WithCustomMessage($"Button {button.Id} carries icon key \"{button.IconKey}\", "
                    + "which is not a ToolbarIconKey member.")
                .That(memberNames.Contains(button.IconKey, StringComparer.Ordinal)).IsTrue();
    }
}
