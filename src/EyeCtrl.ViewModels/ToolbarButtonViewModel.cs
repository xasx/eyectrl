namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ToolbarButtonViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty] private string _symbol;
    [ObservableProperty] private double _dwellProgress;
    [ObservableProperty] private bool _isHighlighted;

    public ToolbarButtonViewModel(string id, string symbol)
    {
        Id = id;
        _symbol = symbol;
    }
}
