namespace EyeCtrl.App.Services;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The right-edge buttons shown while the action menu is open: Close, which dismisses it without
/// performing anything, and Help, which toggles the ring's action names.
///
/// Slots and radius match the manual-selection pair (<c>OverlayVmBindingCoordinator</c>) exactly,
/// so the bar reads the same whichever context owns it.
///
/// Both are icon-only since 2026-10-06 (the user dropped the edge labels for good icons), so each
/// carries an <see cref="EdgeButtonDefinition.AccessibleName"/> for UIA. They remain the two
/// targets that cannot rely on being recognised — one explains the others and the other is the
/// escape hatch — which is exactly why their icons are the boldest in the set.
/// </summary>
internal static class ActionMenuEdgeButtons
{
    internal const string CloseId = "ActionMenu_close";
    internal const string HelpId = "ActionMenu_help";

    /// <summary>Close above the anchor, Help below — the manual-selection layout verbatim.</summary>
    /// <param name="dwellMs">Dwell time for both targets, matched to the ring's own.</param>
    /// <returns>The two definitions, Close first.</returns>
    internal static IReadOnlyList<EdgeButtonDefinition> Definitions(int dwellMs)
    {
        var offset = DpiHelper.LogicalToPhysicalY(
            UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap / 2);

        return new[]
        {
            new EdgeButtonDefinition(
                CloseId, Symbol: string.Empty, dwellMs,
                m => m.Send(new ActionMenuDismissedMessage()),
                VerticalOffset: -offset,
                RadiusLogical: UIConstants.ButtonRadius,
                Icon: ActionIcon.Close,
                AccessibleName: "Close"),
            new EdgeButtonDefinition(
                HelpId, Symbol: string.Empty, dwellMs,
                m => m.Send(new ToggleActionMenuLabelsMessage()),
                VerticalOffset: offset,
                RadiusLogical: UIConstants.ButtonRadius,
                Icon: ActionIcon.Help,
                AccessibleName: "Help"),
        };
    }
}
