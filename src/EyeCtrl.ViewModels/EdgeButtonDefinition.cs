namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;

/// <summary>
/// Immutable declarative definition of one edge-button. Pure data (no WPF).
/// The bar places the button at (edgeAnchor.X, edgeAnchor.Y + VerticalOffset).
/// <paramref name="RadiusLogical"/> is the button radius in logical (DIP) pixels; it drives both
/// the rendered <c>DwellArcButton</c> size and the dwell hit-target. Defaults to the drag button
/// size; manual-selection uses a larger radius (see <see cref="UIConstants.ButtonRadius"/>).
///
/// <paramref name="Icon"/> names a drawn icon to render INSTEAD of <paramref name="Symbol"/>.
/// Icon-only is the edge bar's normal form: every production definition but the modifier bar
/// carries an icon and no label, and each one MUST also carry <paramref name="AccessibleName"/> —
/// the drawn Path has no automation peer, so the name is the only way UIA can announce the button
/// (the definition sweep enforces it). <paramref name="Symbol"/> remains for legacy glyph
/// buttons, which expose their glyph as the name unless one is given.
///
/// <paramref name="Label"/> is the exception, not the rule: only the Shift/Ctrl/Alt modifier bar
/// uses it, stacked under its icon. A labelled button takes the Standard icon size (Medium), never
/// Solo — see <c>EdgeButtonBarWindow</c>'s Solo ternary — and needs no
/// <paramref name="AccessibleName"/>, because the label's text is what gets read.
///
/// <paramref name="RearmOnGazeExit"/> marks a toggle whose activation latches the bar's re-arm
/// gate: until gaze has stayed outside this button's effective region for the re-arm debounce
/// (see <c>EdgeButtonBarViewModel</c>), further samples the detector would award to this button
/// are delivered as if they hit nothing, so a held gaze cannot re-fire it. Default false — every
/// non-toggle definition keeps today's behaviour.
///
/// <paramref name="AccessibleName"/> is the button's UIA name. Required on an icon-only
/// definition (the Path cannot name itself); optional elsewhere, where a visible label or symbol
/// is the fallback. It is set on the button's peer-bearing text element, or on a hidden,
/// disc-sized automation anchor when the button has no text at all.
/// </summary>
public sealed record EdgeButtonDefinition(
    string Id,
    string Symbol,
    int DwellMs,
    Action<IMessenger> Activate,
    double VerticalOffset,
    double RadiusLogical = UIConstants.DragButtonRadius,
    ActionIcon? Icon = null,
    string? Label = null,
    bool RearmOnGazeExit = false,
    string? AccessibleName = null);
