namespace EyeCtrl.App.Tests;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// ToolbarViewModel names its icons as strings because EyeCtrl.ViewModels must not reference the
/// WPF Controls assembly. That string is parsed back to a <see cref="ToolbarIconKey"/> when the
/// toolbar builds its buttons, so a typo is a runtime throw during window construction — in front
/// of a user whose only input channel is an eye tracker. This project sees both sides; it is the
/// only place the two ends can be pinned together.
/// </summary>
public class ToolbarIconKeyBridgeTests
{
    [Fact]
    public void EverySelectionModeIconKey_NamesARealToolbarIconKey()
    {
        foreach (var mode in Enum.GetValues<SelectionMode>())
        {
            var key = SelectionModeIcons.ForMode(mode);
            Check.WithCustomMessage($"SelectionModeIcons.ForMode({mode}) = \"{key}\", which is not "
                    + "a ToolbarIconKey member.")
                .That(Enum.TryParse<ToolbarIconKey>(key, out _)).IsTrue();
        }
    }

    [Fact]
    public void EveryViewModelButtonIconKey_NamesARealToolbarIconKey()
    {
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());

        foreach (var button in vm.Buttons)
            Check.WithCustomMessage($"Button {button.Id} carries icon key \"{button.IconKey}\", "
                    + "which is not a ToolbarIconKey member.")
                .That(Enum.TryParse<ToolbarIconKey>(button.IconKey, out _)).IsTrue();
    }
}
