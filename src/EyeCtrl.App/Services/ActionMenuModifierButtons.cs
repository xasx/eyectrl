namespace EyeCtrl.App.Services;

using System.Collections.Generic;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// The left-edge modifier buttons shown while the action menu is open: Shift, Ctrl, Alt — top
/// to bottom — mirroring the right-edge Close/Help pair. Dwelling one sends
/// <see cref="ToggleActionMenuModifierMessage"/>; the latch lives in
/// <see cref="ActionMenuViewModel"/> and dies with the menu session (one-shot, ledger L2).
/// Labels are always shown: the ActionMenuEdgeButtons convention for targets a user cannot yet
/// rely on recognising.
/// </summary>
internal static class ActionMenuModifierButtons
{
    internal const string ShiftId = "ActionMenu_mod_Shift";
    internal const string CtrlId = "ActionMenu_mod_Ctrl";
    internal const string AltId = "ActionMenu_mod_Alt";

    /// <summary>The three definitions for the current latch state. Offsets and spacing mirror
    /// ActionMenuEdgeButtons: physical-Y conversion, spacing 2·radius + the side-button gap.</summary>
    internal static IReadOnlyList<EdgeButtonDefinition> Definitions(int dwellMs, IReadOnlySet<ModifierKey> latched)
    {
        var spacing = DpiHelper.LogicalToPhysicalY(2 * UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap);
        return new[]
        {
            Def(ShiftId, ModifierKey.Shift, ActionIcon.Shift, ActionIcon.ShiftLatched, -spacing, dwellMs, latched),
            Def(CtrlId, ModifierKey.Ctrl, ActionIcon.Ctrl, ActionIcon.CtrlLatched, 0, dwellMs, latched),
            Def(AltId, ModifierKey.Alt, ActionIcon.Alt, ActionIcon.AltLatched, +spacing, dwellMs, latched),
        };
    }

    /// <summary>The latched-variant swap for ONE button — used by the coordinator's
    /// LatchedModifiers branch (one <c>ReplaceButton</c> per toggle, per the
    /// once-per-logical-mutation contract).</summary>
    internal static EdgeButtonDefinition WithLatched(EdgeButtonDefinition definition, bool latched) => definition.Id switch
    {
        ShiftId => definition with { Icon = latched ? ActionIcon.ShiftLatched : ActionIcon.Shift },
        CtrlId => definition with { Icon = latched ? ActionIcon.CtrlLatched : ActionIcon.Ctrl },
        AltId => definition with { Icon = latched ? ActionIcon.AltLatched : ActionIcon.Alt },
        _ => definition,
    };

    internal static string IdFor(ModifierKey modifier) => modifier switch
    {
        ModifierKey.Shift => ShiftId,
        ModifierKey.Ctrl => CtrlId,
        ModifierKey.Alt => AltId,
        _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, "The mod bar offers Shift/Ctrl/Alt only."),
    };

    private static EdgeButtonDefinition Def(string id, ModifierKey modifier, ActionIcon icon, ActionIcon latchedIcon, double verticalOffset, int dwellMs, IReadOnlySet<ModifierKey> latched)
        => new(
            id, Symbol: string.Empty, dwellMs,
            Activate: m => m.Send(new ToggleActionMenuModifierMessage(modifier)),
            VerticalOffset: verticalOffset,
            RadiusLogical: UIConstants.ButtonRadius,
            Icon: latched.Contains(modifier) ? latchedIcon : icon,
            Label: id.EndsWith("Shift") ? "Shift" : id.EndsWith("Ctrl") ? "Ctrl" : "Alt",
            RearmOnGazeExit: true);
}
