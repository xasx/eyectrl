namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// Immutable declarative definition of one edge-button. Pure data (no WPF).
/// The bar places the button at (edgeAnchor.X, edgeAnchor.Y + VerticalOffset).
/// </summary>
public sealed record EdgeButtonDefinition(
    string Id,
    string Symbol,
    int DwellMs,
    Action<IMessenger> Activate,
    double VerticalOffset);
