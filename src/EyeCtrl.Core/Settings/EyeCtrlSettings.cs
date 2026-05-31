namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class EyeCtrlSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private CursorSettings _cursor = new();
    [ObservableProperty] private DwellSettings _dwell = new();
    [ObservableProperty] private ManualSelectionSettings _ManualSelection = new();
    [ObservableProperty] private ActionMenuSettings _actionMenu = new();
    [ObservableProperty] private KeyboardSettings _keyboard = new();
    [ObservableProperty] private ToolbarSettings _toolbar = new();
    [ObservableProperty] private ThemeSettings _theme = new();
    [ObservableProperty] private BackdropSettings _backdrop = new();
    [ObservableProperty] private CalibrationSettings _calibration = new();
    [ObservableProperty] private GeneralSettings _general = new();
    [ObservableProperty] private ViewPosition _viewPosition = ViewPosition.AtCenter;
    [ObservableProperty] private SelectionMode _selectionMode = SelectionMode.None;
    [ObservableProperty] private AutoSelectionSettings _autoSelection = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        // Validate all sub-settings (handle unexpected nulls explicitly)
        if (Cursor is null)
            errors.Add("Cursor settings must not be null.");
        else
            errors.AddRange(Cursor.Validate());

        if (Dwell is null)
            errors.Add("Dwell settings must not be null.");
        else
            errors.AddRange(Dwell.Validate());

        if (ManualSelection is null)
            errors.Add("ManualSelection settings must not be null.");
        else
            errors.AddRange(ManualSelection.Validate());

        if (ActionMenu is null)
            errors.Add("Action menu settings must not be null.");
        else
            errors.AddRange(ActionMenu.Validate());

        if (Keyboard is null)
            errors.Add("Keyboard settings must not be null.");
        else
            errors.AddRange(Keyboard.Validate());

        if (Toolbar is null)
            errors.Add("Toolbar settings must not be null.");
        else
            errors.AddRange(Toolbar.Validate());

        if (Theme is null)
            errors.Add("Theme settings must not be null.");
        else
            errors.AddRange(Theme.Validate());

        if (Backdrop is null)
            errors.Add("Backdrop settings must not be null.");
        else
            errors.AddRange(Backdrop.Validate());

        if (Calibration is null)
            errors.Add("Calibration settings must not be null.");
        else
            errors.AddRange(Calibration.Validate());

        if (General is null)
            errors.Add("General settings must not be null.");
        else
            errors.AddRange(General.Validate());

        if (AutoSelection is null)
            errors.Add("AutoSelection settings must not be null.");
        else
            errors.AddRange(AutoSelection.Validate());

        return errors;
    }
}
