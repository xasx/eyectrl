namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ToolbarSettings : ObservableObject
{
    [ObservableProperty] private bool _isDocked = true;
    [ObservableProperty] private string _dockedEdge = "Top";
    [ObservableProperty] private double _size = 80;
}
