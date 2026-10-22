namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Observable runtime state for one visible edge-button. The View binds to this
/// (one instance per button in <see cref="EdgeButtonBarViewModel"/>).
/// </summary>
public partial class EdgeButtonState : ObservableObject
{
    public string Id { get; }
    public string Symbol { get; }

    /// <summary>Button radius in logical (DIP) pixels — used by the View to size the DwellArcButton.</summary>
    public double RadiusLogical { get; }

    [ObservableProperty] private double _dwellProgress;
    /// <summary>
    /// True while this item's dwell is HELD: the gaze stepped off mid-dwell and <see cref="DwellProgress"/>
    /// is kept for the pause grace. The view dims the progress indicator; it never changes the value.
    /// Display-only — nothing reads it to decide whether a dwell holds.
    /// </summary>
    [ObservableProperty] private bool _isDwellPaused;

    public EdgeButtonState(string id, string symbol, double radiusLogical)
    {
        Id = id;
        Symbol = symbol;
        RadiusLogical = radiusLogical;
    }
}
