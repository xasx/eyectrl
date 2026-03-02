namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class EyeCtrlSettings : ObservableObject
{
    [ObservableProperty] private CursorSettings _cursor = new();
    [ObservableProperty] private DwellSettings _dwell = new();
    [ObservableProperty] private ZoomSettings _zoom = new();
    [ObservableProperty] private ActionMenuSettings _actionMenu = new();
    [ObservableProperty] private KeyboardSettings _keyboard = new();
    [ObservableProperty] private ToolbarSettings _toolbar = new();
    [ObservableProperty] private ThemeSettings _theme = new();
    [ObservableProperty] private GeneralSettings _general = new();
}
