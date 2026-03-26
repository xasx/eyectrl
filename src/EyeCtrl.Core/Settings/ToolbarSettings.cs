namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ToolbarSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _isDocked = true;
    [ObservableProperty] private string _dockedEdge = "Top";
    [ObservableProperty] private double _size = 80;

    private static readonly string[] ValidDockedEdges = { "Top", "Bottom", "Left", "Right" };

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateStringEnum(DockedEdge, ValidDockedEdges, nameof(DockedEdge)),
            ValidationHelper.ValidateRange(Size, 40.0, 200.0, nameof(Size))
        );
    }
}
