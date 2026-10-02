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
/// <paramref name="Icon"/> names a drawn icon to render INSTEAD of <paramref name="Symbol"/>, and
/// <paramref name="Label"/> a name to show beneath it. Both optional and last, so every existing
/// definition keeps its glyph and is not edited.
///
/// <paramref name="Label"/> lives on the definition rather than being special-cased in the window
/// because two buttons need it, and a second special case is how a convention becomes a mess.
///
/// <paramref name="RearmOnGazeExit"/> marks a toggle whose activation latches the bar's re-arm
/// gate: until gaze has stayed outside this button's effective region for the re-arm debounce
/// (see <c>EdgeButtonBarViewModel</c>), further samples the detector would award to this button
/// are delivered as if they hit nothing, so a held gaze cannot re-fire it. Default false — every
/// non-toggle definition keeps today's behaviour.
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
    bool RearmOnGazeExit = false);
