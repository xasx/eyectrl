namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ToolbarButtonViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty] private string _iconKey;
    [ObservableProperty] private double _dwellProgress;
    /// <summary>
    /// True while this item's dwell is HELD: the gaze stepped off mid-dwell and <see cref="DwellProgress"/>
    /// is kept for the pause grace. The view dims the progress indicator; it never changes the value.
    /// Display-only — nothing reads it to decide whether a dwell holds.
    /// </summary>
    [ObservableProperty] private bool _isDwellPaused;
    [ObservableProperty] private bool _isHighlighted;

    public ToolbarButtonViewModel(string id, string iconKey)
    {
        Id = id;
        _iconKey = iconKey;
    }
}
