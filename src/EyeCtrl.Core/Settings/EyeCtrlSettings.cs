namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

/// <summary>Root settings aggregate containing all application settings categories.</summary>
public partial class EyeCtrlSettings : ObservableObject, IValidatable
{
    /// <summary>Cursor filtering and appearance settings.</summary>
    [ObservableProperty] private CursorSettings _cursor = new();
    /// <summary>Dwell timing settings for click activation.</summary>
    [ObservableProperty] private DwellSettings _dwell = new();
    /// <summary>Manual (zoom-to-point) selection settings.</summary>
    [ObservableProperty] private ManualSelectionSettings _ManualSelection = new();
    /// <summary>Action menu visibility and order settings.</summary>
    [ObservableProperty] private ActionMenuSettings _actionMenu = new();
    /// <summary>Keyboard layout and typing settings.</summary>
    [ObservableProperty] private KeyboardSettings _keyboard = new();
    /// <summary>Toolbar appearance and dock settings.</summary>
    [ObservableProperty] private ToolbarSettings _toolbar = new();
    /// <summary>Theme and overlay opacity settings.</summary>
    [ObservableProperty] private ThemeSettings _theme = new();
    /// <summary>Backdrop material and opacity settings.</summary>
    [ObservableProperty] private BackdropSettings _backdrop = new();
    /// <summary>Eye tracker calibration settings.</summary>
    [ObservableProperty] private CalibrationSettings _calibration = new();
    /// <summary>General application behavior settings.</summary>
    [ObservableProperty] private GeneralSettings _general = new();
    /// <summary>Default view position for overlays (at gaze or at screen center).</summary>
    [ObservableProperty] private ViewPosition _viewPosition = ViewPosition.AtCenter;
    /// <summary>Gaze-based click targeting mode.</summary>
    [ObservableProperty] private SelectionMode _selectionMode = SelectionMode.None;
    /// <summary>Automatic control selection (snap-to-control) settings.</summary>
    [ObservableProperty] private AutoSelectionSettings _autoSelection = new();
    /// <summary>External (hardware-button) dwell activation settings.</summary>
    [ObservableProperty] private ExternalDwellSettings _externalDwell = new();
    /// <summary>Gaze-joystick scroll mode settings.</summary>
    [ObservableProperty] private ScrollSettings _scroll = new();
    /// <summary>Eye-care break reminder and exercise settings.</summary>
    [ObservableProperty] private EyeCareSettings _eyeCare = new();
    /// <summary>Gaze trail drag-and-drop settings.</summary>
    [ObservableProperty] private DragSettings _drag = new();

    /// <inheritdoc />
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

        if (ExternalDwell is null)
            errors.Add("ExternalDwell settings must not be null.");
        else
            errors.AddRange(ExternalDwell.Validate());

        if (Scroll is null)
            errors.Add("Scroll settings must not be null.");
        else
            errors.AddRange(Scroll.Validate());

        if (EyeCare is null)
            errors.Add("EyeCare settings must not be null.");
        else
            errors.AddRange(EyeCare.Validate());

        if (Drag is null)
            errors.Add("Drag settings must not be null.");
        else
            errors.AddRange(Drag.Validate());

        return errors;
    }
}
