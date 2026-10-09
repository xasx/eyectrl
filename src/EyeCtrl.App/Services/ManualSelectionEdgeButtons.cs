namespace EyeCtrl.App.Services;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The right-edge buttons shown during manual (zoom-to-point) selection: Close, which exits back
/// to free look, and Recapture, which re-shoots the magnifier at the same fixation point.
///
/// Extracted from <c>OverlayVmBindingCoordinator</c> to mirror <see cref="ActionMenuEdgeButtons"/>'s
/// shape (a static <c>Definitions(dwellMs)</c> factory) rather than staying inline, so the plain
/// data driving both edge-button pairs is testable the same way.
///
/// Both are icon-only since 2026-10-06 — the drawn camera (user pick Re3) replaced Retake, whose
/// label is gone with the rest of the edge labels — so each carries an
/// <see cref="EdgeButtonDefinition.AccessibleName"/> for UIA. Close is the escape hatch and reuses
/// <see cref="ActionIcon.Close"/> rather than a second drawn icon for the same glyph.
/// </summary>
internal static class ManualSelectionEdgeButtons
{
    internal const string CloseId = "ManualSelection_close";
    internal const string RecaptureId = "ManualSelection_recapture";

    /// <summary>
    /// Close above the anchor, Recapture below. Full-size buttons (<see cref="UIConstants.ButtonRadius"/>)
    /// separated so their edges are <see cref="UIConstants.ManualSelectionSideButtonGap"/> apart,
    /// centred on the anchor — unchanged from the original inline layout.
    /// </summary>
    /// <param name="dwellMs">Dwell time for both targets.</param>
    /// <returns>The two definitions, Close first.</returns>
    internal static IReadOnlyList<EdgeButtonDefinition> Definitions(int dwellMs)
    {
        var radius = UIConstants.ButtonRadius;
        var offset = DpiHelper.LogicalToPhysicalY(radius + UIConstants.ManualSelectionSideButtonGap / 2);

        return new[]
        {
            new EdgeButtonDefinition(
                CloseId, Symbol: "✕", dwellMs,
                m => m.Send(new ManualSelectionCloseMessage()),
                VerticalOffset: -offset,
                RadiusLogical: radius,
                Icon: ActionIcon.Close,
                AccessibleName: StringTable.Current.T("overlays.edge.close")),
            new EdgeButtonDefinition(
                RecaptureId, Symbol: string.Empty, dwellMs,
                m => m.Send(new ManualSelectionRecaptureMessage()),
                VerticalOffset: offset,
                RadiusLogical: radius,
                Icon: ActionIcon.Recapture,
                AccessibleName: StringTable.Current.T("overlays.edge.recapture")),
        };
    }
}
