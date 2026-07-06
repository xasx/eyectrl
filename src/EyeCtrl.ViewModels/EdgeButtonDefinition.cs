namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// Immutable declarative definition of one edge-button. Pure data (no WPF).
/// The bar places the button at (edgeAnchor.X, edgeAnchor.Y + VerticalOffset).
/// <paramref name="RadiusLogical"/> is the button radius in logical (DIP) pixels; it drives both
/// the rendered <c>DwellArcButton</c> size and the dwell hit-target. Defaults to the drag button
/// size; manual-selection uses a larger radius (see <see cref="UIConstants.ButtonRadius"/>).
/// </summary>
public sealed record EdgeButtonDefinition(
    string Id,
    string Symbol,
    int DwellMs,
    Action<IMessenger> Activate,
    double VerticalOffset,
    double RadiusLogical = UIConstants.DragButtonRadius);
