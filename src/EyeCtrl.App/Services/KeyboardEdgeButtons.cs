namespace EyeCtrl.App.Services;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The right-edge pair shown while the keyboard is up: Close, which dismisses it and restores
/// fixation, and Flip, which moves it to the opposite screen edge (spec B4, ledgers #6/#12/#15).
///
/// The bar TRACKS the keyboard (ledger #15, mechanism B): the anchor sits toward the
/// keyboard's edge (25%/75% height), not at screen center — the caller computes it via
/// <c>ScreenGeometry.KeyboardEdgeBarAnchor</c>. The SLOT ORDER is fixed and never flips with
/// the keyboard (owner pass 2026-09-16: "the buttons shall not flip. Close always above
/// flip"): Close above, Flip below, at both edges. On a flip the vacated positions carry no
/// dwell target, so a gaze resting where a button just was hits nothing.
///
/// Both keep their labels at all times (the ActionMenuEdgeButtons convention: the escape hatch
/// cannot rely on being recognised).
/// </summary>
internal static class KeyboardEdgeButtons
{
    internal const string CloseId = "Keyboard_close";
    internal const string FlipId = "Keyboard_flip";

    /// <summary>Right-edge inset, same value the other edge-bar callers reserve (their private
    /// RightEdgeInsetLogical = 30). Ours so ScreenGeometry's keyboard variant can use it.</summary>
    internal const double RightEdgeInsetLogical = 30;

    /// <param name="dwellMs">Keyboard key dwell — the bar is part of the keyboard session.</param>
    internal static IReadOnlyList<EdgeButtonDefinition> Definitions(int dwellMs)
    {
        var offset = DpiHelper.LogicalToPhysicalY(
            UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap / 2);

        return new[]
        {
            new EdgeButtonDefinition(
                CloseId, Symbol: string.Empty, dwellMs,
                m => m.Send(new ViewModels.Messages.KeyboardHideRequestedMessage()),
                VerticalOffset: -offset,
                RadiusLogical: UIConstants.ButtonRadius,
                Icon: ActionIcon.Close,
                Label: "Close"),
            new EdgeButtonDefinition(
                FlipId, Symbol: string.Empty, dwellMs,
                m => m.Send(new ViewModels.Messages.KeyboardEdgeToggledMessage()),
                VerticalOffset: offset,
                RadiusLogical: UIConstants.ButtonRadius,
                Icon: ActionIcon.KeyboardEdge,
                Label: "Flip"),
        };
    }
}
