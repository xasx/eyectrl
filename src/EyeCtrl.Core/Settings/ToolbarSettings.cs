namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Toolbar appearance and docking settings.</summary>
public partial class ToolbarSettings : ObservableObject, IValidatable
{
    /// <summary>Whether the toolbar is docked to a screen edge.</summary>
    [ObservableProperty] private bool _isDocked = true;
    /// <summary>Screen edge where the toolbar is docked (Top, Bottom, Left, Right).</summary>
    [ObservableProperty] private string _dockedEdge = "Top";
    /// <summary>Size of the toolbar in logical pixels. Valid range: 40–200.</summary>
    [ObservableProperty] private double _size = 80;
    /// <summary>Whether the toolbar shows the Minimize/MaximizeRestore/Close region. Default true.</summary>
    [ObservableProperty] private bool _showWindowActions = true;

    private static readonly string[] ValidDockedEdges = { "Top", "Bottom", "Left", "Right" };

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateStringEnum(DockedEdge, ValidDockedEdges, nameof(DockedEdge)),
            ValidationHelper.ValidateRange(Size, 40.0, 200.0, nameof(Size))
        );
    }
}
