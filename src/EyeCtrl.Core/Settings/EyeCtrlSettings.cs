namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class EyeCtrlSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private CursorSettings _cursor = new();
    [ObservableProperty] private DwellSettings _dwell = new();
    [ObservableProperty] private ZoomSettings _zoom = new();
    [ObservableProperty] private ActionMenuSettings _actionMenu = new();
    [ObservableProperty] private KeyboardSettings _keyboard = new();
    [ObservableProperty] private ToolbarSettings _toolbar = new();
    [ObservableProperty] private ThemeSettings _theme = new();
    [ObservableProperty] private GeneralSettings _general = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        // Validate all sub-settings
        errors.AddRange(Cursor.Validate());
        errors.AddRange(Dwell.Validate());
        errors.AddRange(Zoom.Validate());
        errors.AddRange(ActionMenu.Validate());
        errors.AddRange(Keyboard.Validate());
        errors.AddRange(Toolbar.Validate());
        errors.AddRange(Theme.Validate());
        errors.AddRange(General.Validate());

        return errors;
    }
}
