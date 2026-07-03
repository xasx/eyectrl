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
    [ObservableProperty] private ManualSelectionSettings _manualSelection = new();
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

    /// <summary>
    /// Enumerates every sub-settings object with its property name. Single source of truth
    /// consumed by Validate, SettingsViewModel.ResetAllSettings, and other
    /// full-coverage consumers. MUST be extended when adding a new sub-settings class
    /// (the drift-guard test EyeCtrlSettingsEnumerationTests enforces this).
    /// </summary>
    public IEnumerable<(string Name, object Settings)> EnumerateSubSettings()
    {
        yield return (nameof(Cursor), Cursor);
        yield return (nameof(Dwell), Dwell);
        yield return (nameof(ManualSelection), ManualSelection);
        yield return (nameof(ActionMenu), ActionMenu);
        yield return (nameof(Keyboard), Keyboard);
        yield return (nameof(Toolbar), Toolbar);
        yield return (nameof(Theme), Theme);
        yield return (nameof(Backdrop), Backdrop);
        yield return (nameof(Calibration), Calibration);
        yield return (nameof(General), General);
        yield return (nameof(AutoSelection), AutoSelection);
        yield return (nameof(ExternalDwell), ExternalDwell);
        yield return (nameof(Scroll), Scroll);
        yield return (nameof(EyeCare), EyeCare);
        yield return (nameof(Drag), Drag);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        foreach (var (name, settings) in EnumerateSubSettings())
        {
            if (settings is null)
                errors.Add($"{name} settings must not be null.");
            else if (settings is IValidatable validatable)
                errors.AddRange(validatable.Validate());
        }

        return errors;
    }
}
