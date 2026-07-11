namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Settings for the Track Status overlay box.</summary>
public partial class TrackStatusSettings : ObservableObject, IValidatable
{
    /// <summary>Whether the track status overlay is shown.</summary>
    [ObservableProperty] private bool _enabled = false;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        // TrackStatusSettings has no numeric ranges to validate
        return new List<string>();
    }
}
