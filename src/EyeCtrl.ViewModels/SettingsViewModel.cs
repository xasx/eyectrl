namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class SettingsViewModel : ObservableObject, IRecipient<CalibrationCompletedMessage>, IDisposable
{
    private const int ItemsPerPage = 6;

    private readonly EyeCtrlSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly IMessenger _messenger;
    private ReadOnlyLabelSettingItem? _calStatusItem;
    private bool _disposed;

    [ObservableProperty] private int _currentPage;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private string _currentTabName = "";

    public List<SettingsTabViewModel> Tabs { get; } = new();
    public ObservableCollection<SettingItemViewModel> CurrentPageItems { get; } = new();

    public SettingsViewModel(EyeCtrlSettings settings, SettingsService settingsService, IMessenger messenger)
    {
        _settings = settings;
        _settingsService = settingsService;
        _messenger = messenger;
        _messenger.RegisterAll(this);
        BuildTabs();
    }

    private void BuildTabs()
    {
        // INTENTIONALLY NOT EXPOSED in the settings UI (audit this list when adding settings):
        //  - Cursor.MaxGapInterpolationMs, Cursor.PositionIndicatorStyle  (advanced tuning)
        //  - ManualSelection.Enabled (driven by SelectionMode), ManualSelection.GazeIndicator* (visual tuning)
        //  - Keyboard ML/model settings (CustomModels etc. — managed by the model registry)
        //  - Calibration.TargetColor (visual tuning)
        // Everything else in EyeCtrlSettings.EnumerateSubSettings() should have a tab/control here.

        var c = _settings.Cursor;
        var d = _settings.Dwell;
        var z = _settings.ManualSelection;
        var k = _settings.Keyboard;
        var t = _settings.Toolbar;
        var g = _settings.General;
        var th = _settings.Theme;
        var cal = _settings.Calibration;

        Tabs.Add(new SettingsTabViewModel("Cursor", "\uD83D\uDDB1", new List<SettingItemViewModel>
        {
            new ComboSettingViewModel("filterMode", "Filter Mode", c.FilterMode.ToString(),
                Enum.GetNames<GazeFilterMode>(), v => c.FilterMode = Enum.Parse<GazeFilterMode>(v)),
            new SliderSettingViewModel("smoothing", "Smoothing", c.SmoothingFactor, 0, 1, 0.1, v => c.SmoothingFactor = v),
            new SliderSettingViewModel("oneEuroMin", "OneEuro Min Cutoff", c.OneEuroMinCutoff, 0.1, 20, 0.1, v => c.OneEuroMinCutoff = v),
            new SliderSettingViewModel("oneEuroBeta", "OneEuro Beta", c.OneEuroBeta, 0, 5, 0.1, v => c.OneEuroBeta = v),
            new SliderSettingViewModel("snapRadius", "Snap Radius", c.SnapRadiusPixels, 0, 100, 1, v => c.SnapRadiusPixels = (int)v),
            new ColorSettingViewModel("circleColor", "Circle Color", c.CircleColor, v => c.CircleColor = v),
            new SliderSettingViewModel("circleOpacity", "Circle Opacity", c.CircleOpacity, 0, 1, 0.1, v => c.CircleOpacity = v),
            new SliderSettingViewModel("circleThickness", "Circle Thickness", c.CircleThickness, 1, 10, 0.5, v => c.CircleThickness = v),
            new SliderSettingViewModel("circleSize", "Circle Size", c.CircleSize, 10, 500, 10, v => c.CircleSize = v),
            new ToggleSettingViewModel("highContrast", "High Contrast", c.CircleHighContrast, v => c.CircleHighContrast = v),
        }));

        Tabs.Add(new SettingsTabViewModel("Dwell", "\u23F1", new List<SettingItemViewModel>
        {
            new SliderSettingViewModel("fixationMs", "Fixation Detection (ms)", d.FixationDetectionMs, 100, 1000, 50, v => d.FixationDetectionMs = (int)v),
            new SliderSettingViewModel("activationMs", "Activation Button Dwell (ms)", d.ActivationButtonDwellMs, 200, 2000, 50, v => d.ActivationButtonDwellMs = (int)v),
            new SliderSettingViewModel("actionMenuMs", "Action Menu Dwell (ms)", d.ActionMenuDwellMs, 200, 2000, 50, v => d.ActionMenuDwellMs = (int)v),
            new SliderSettingViewModel("ManualSelectionActionMs", "ManualSelection Action Menu Dwell (ms)", d.ManualSelectionActionMenuDwellMs, 200, 2000, 50, v => d.ManualSelectionActionMenuDwellMs = (int)v),
            new SliderSettingViewModel("toolbarMs", "Toolbar Button Dwell (ms)", d.ToolbarButtonDwellMs, 200, 2000, 50, v => d.ToolbarButtonDwellMs = (int)v),
            new SliderSettingViewModel("keyboardMs", "Keyboard Key Dwell (ms)", d.KeyboardKeyDwellMs, 100, 1500, 50, v => d.KeyboardKeyDwellMs = (int)v),
            new SliderSettingViewModel("predictionMs", "Prediction Dwell (ms)", d.KeyboardPredictionDwellMs, 100, 1500, 50, v => d.KeyboardPredictionDwellMs = (int)v),
            new SliderSettingViewModel("fixationRadius", "Fixation Radius (px)", d.FixationRadiusPixels, 10, 200, 5, v => d.FixationRadiusPixels = v),
            new SliderSettingViewModel("velocityThreshold", "Velocity Threshold (px/s)", d.VelocityThresholdPxPerSec, 10, 1000, 10, v => d.VelocityThresholdPxPerSec = v),
            new SliderSettingViewModel("dwellCooldownMs", "Dwell Cooldown (ms)", d.DwellCooldownMs, 100, 5000, 50, v => d.DwellCooldownMs = (int)v),
            new SliderSettingViewModel("dragReleaseMs", "Drag Release Dwell (ms)", d.DragReleaseButtonDwellMs, 200, 2000, 50, v => d.DragReleaseButtonDwellMs = (int)v),
            new SliderSettingViewModel("dragRightEdgeMs", "Drag Edge Button Dwell (ms)", d.DragRightEdgeButtonDwellMs, 200, 2000, 50, v => d.DragRightEdgeButtonDwellMs = (int)v),
        }));

        var ed = _settings.ExternalDwell;
        Tabs.Add(new SettingsTabViewModel("ExternalDwell", "\uD83D\uDD17", new List<SettingItemViewModel>
        {
            new ToggleSettingViewModel("externalEnabled", "Enabled", ed.Enabled, v => ed.Enabled = v),
            new SliderSettingViewModel("externalDwell", "Dwell Time (ms)", ed.DefaultDwellMs, 200, 3000, 50, v => ed.DefaultDwellMs = (int)v),
            new SliderSettingViewModel("externalCooldown", "Cooldown (ms)", ed.CooldownMs, 0, 5000, 50, v => ed.CooldownMs = (int)v),
        }));

        var s = _settings.Scroll;
        Tabs.Add(new SettingsTabViewModel("Scroll", "\uD83D\uDDA5", new List<SettingItemViewModel>
        {
            new SliderSettingViewModel("scrollSensitivity", "Sensitivity", s.Sensitivity, 0.5, 2.0, 0.1, v => s.Sensitivity = v),
        }));

        var highlightColor = new ColorSettingViewModel("highlightColor", "Highlight Color",
            _settings.AutoSelection.HighlightColor, v => _settings.AutoSelection.HighlightColor = v);
        var highlightThickness = new SliderSettingViewModel("highlightThickness", "Highlight Thickness",
            _settings.AutoSelection.HighlightThickness, 1, 10, 1, v => _settings.AutoSelection.HighlightThickness = v);
        highlightColor.IsEnabled = highlightThickness.IsEnabled = (_settings.SelectionMode == SelectionMode.AutoSelection);

        Tabs.Add(new SettingsTabViewModel("Selection", "\uD83C\uDFAF", new List<SettingItemViewModel>
        {
            new ComboSettingViewModel("selectionMode", "Selection Mode",
                _settings.SelectionMode.ToString(),
                Enum.GetNames<SelectionMode>(),
                v =>
                {
                    _settings.SelectionMode = Enum.Parse<SelectionMode>(v);
                    highlightColor.IsEnabled = highlightThickness.IsEnabled =
                        (_settings.SelectionMode == SelectionMode.AutoSelection);
                }),
            new SliderSettingViewModel("ManualSelectionRadius", "Circle Radius (px)",
                z.CircleRadiusPixels, 50, 500, 10, v => z.CircleRadiusPixels = (int)v),
            new SliderSettingViewModel("magnification", "Magnification Level",
                z.MagnificationLevel, 1.5, 5, 0.5, v => z.MagnificationLevel = v),
            highlightColor,
            highlightThickness,
        }));

        Tabs.Add(new SettingsTabViewModel("Actions", "\u2699", new List<SettingItemViewModel>
        {
            // Action menu items are managed differently -- just a placeholder tab
        }));

        Tabs.Add(new SettingsTabViewModel("Keyboard", "\u2328", new List<SettingItemViewModel>
        {
            new ComboSettingViewModel("layout", "Layout", k.ActiveLayout, new[] { "QWERTY", "AZERTY", "QWERTZ" }, v => k.ActiveLayout = v),
            new SliderSettingViewModel("keySize", "Key Size Multiplier", k.KeySizeMultiplier, 0.5, 2.0, 0.1, v => k.KeySizeMultiplier = v),
            new ToggleSettingViewModel("prediction", "Word Prediction", k.PredictionEnabled, v => k.PredictionEnabled = v),
            new ComboSettingViewModel("language", "Language Pack", k.LanguagePack, new[] { "en", "de", "fr", "es" }, v => k.LanguagePack = v),
            new ComboSettingViewModel("screenEdge", "Screen Edge", k.ScreenEdge, new[] { "Top", "Bottom" }, v => k.ScreenEdge = v),
        }));

        Tabs.Add(new SettingsTabViewModel("Toolbar", "\uD83D\uDEE0", new List<SettingItemViewModel>
        {
            new ToggleSettingViewModel("docked", "Docked", t.IsDocked, v => t.IsDocked = v),
            new ComboSettingViewModel("dockedEdge", "Docked Edge", t.DockedEdge, new[] { "Top", "Bottom", "Left", "Right" }, v => t.DockedEdge = v),
            new SliderSettingViewModel("toolbarSize", "Button Size", t.Size, 40, 200, 10, v => t.Size = v),
        }));

        var b = _settings.Backdrop;
        Tabs.Add(new SettingsTabViewModel("Backdrop", "▣", new List<SettingItemViewModel>
        {
            new ToggleSettingViewModel("backdropEnabled", "Enabled", b.Enabled, v => b.Enabled = v),
            new ComboSettingViewModel("backdropType", "Material", b.Type.ToString(),
                Enum.GetNames<BackdropType>(), v => b.Type = Enum.Parse<BackdropType>(v)),
            new SliderSettingViewModel("backdropOpacity", "Opacity", b.Opacity, 0.1, 0.9, 0.05, v => b.Opacity = v),
        }));

        var dr = _settings.Drag;
        Tabs.Add(new SettingsTabViewModel("Drag", "✊", new List<SettingItemViewModel>
        {
            new SliderSettingViewModel("dragSettleMs", "Settle Dwell (ms)", dr.SettleDwellMs, 200, 2000, 50, v => dr.SettleDwellMs = (int)v),
            new SliderSettingViewModel("dragSettleRadius", "Settle Radius (px)", dr.SettleRadiusPixels, 10, 200, 5, v => dr.SettleRadiusPixels = (int)v),
            new ColorSettingViewModel("dragTrailColor", "Trail Color", dr.TrailColor, v => dr.TrailColor = v),
            new SliderSettingViewModel("dragTrailOpacity", "Trail Opacity", dr.TrailOpacity, 0.1, 1.0, 0.1, v => dr.TrailOpacity = v),
        }));

        Tabs.Add(new SettingsTabViewModel("General", "\u2699", new List<SettingItemViewModel>
        {
            new ToggleSettingViewModel("startOnBoot", "Start on Boot", g.StartOnBoot, v => g.StartOnBoot = v),
            new ToggleSettingViewModel("startPaused", "Start Paused", g.StartPaused, v => g.StartPaused = v),
            new ComboSettingViewModel("themeMode", "Theme", th.Mode, new[] { "System", "Light", "Dark" }, v => th.Mode = v),
            new SliderSettingViewModel("overlayOpacity", "Overlay Opacity", th.OverlayOpacity, 0.1, 1.0, 0.1, v => th.OverlayOpacity = v),
            new ToggleSettingViewModel("themeHighContrast", "High Contrast", th.HighContrast, v => th.HighContrast = v),
        }));

        // NOTE: item count here only drives pagination (ItemsPerPage = 6 → 2 pages);
        // the actual interactive controls are built in SettingsWindow.BuildCalibrationPages.
        var calItems = new List<SettingItemViewModel>
        {
            new SliderSettingViewModel("calPointCount", "Point Count", cal.PointCount, 3, 16, 1, v => cal.PointCount = (int)v),
            new SliderSettingViewModel("calSamples", "Samples per Point", cal.SamplesPerPoint, 5, 100, 5, v => cal.SamplesPerPoint = (int)v),
            new SliderSettingViewModel("calHoldMs", "Target Hold (ms)", cal.TargetHoldMs, 200, 3000, 100, v => cal.TargetHoldMs = (int)v),
            new SliderSettingViewModel("calSettleMs", "Settle Window (ms)", cal.SettleWindowMs, 0, 500, 25, v => cal.SettleWindowMs = (int)v),
            new ActionButtonSettingItem("calCalibrate", "Calibrate Now",
                () => _messenger.Send(new CalibrationRequestedMessage())),
            new ActionButtonSettingItem("calClear", "Clear Calibration",
                () => _messenger.Send(new CalibrationClearRequestedMessage())),
        };
        _calStatusItem = new ReadOnlyLabelSettingItem("calStatus", "Last Calibration", "No calibration data");
        calItems.Add(_calStatusItem);
        Tabs.Add(new SettingsTabViewModel("Calibration", "\u2316", calItems));

        var ec = _settings.EyeCare;
        Tabs.Add(new SettingsTabViewModel("Eye Care", "\U0001F441", new List<SettingItemViewModel>
        {
            new ToggleSettingViewModel("ecEnabled", "Enable eye-care breaks", ec.Enabled, v => ec.Enabled = v),
            new SliderSettingViewModel("ecInterval", "Break interval (min)", ec.IntervalMinutes, 1, 120, 1, v => ec.IntervalMinutes = (int)v),
            new SliderSettingViewModel("ecNatural", "Natural break threshold (s)", ec.NaturalBreakSeconds, 5, 600, 5, v => ec.NaturalBreakSeconds = (int)v),
            new SliderSettingViewModel("ecDuration", "Exercise duration (s)", ec.ExerciseDurationSeconds, 10, 180, 5, v => ec.ExerciseDurationSeconds = (int)v),
            new SliderSettingViewModel("ecAutoSnooze", "Banner auto-snooze (s)", ec.BannerAutoSnoozeSeconds, 5, 120, 5, v => ec.BannerAutoSnoozeSeconds = (int)v),
            new SliderSettingViewModel("ecSnooze", "Snooze length (min)", ec.SnoozeMinutes, 1, 60, 1, v => ec.SnoozeMinutes = (int)v),
            new ToggleSettingViewModel("ecFatigue", "Enable fatigue escalation (experimental)", ec.FatigueMonitorEnabled, v => ec.FatigueMonitorEnabled = v),
            new SliderSettingViewModel("ecFatigueThreshold", "Fatigue threshold", ec.FatigueThreshold, 0.0, 1.0, 0.05, v => ec.FatigueThreshold = v),
        }));
    }

    public void ShowTab(string tabName)
    {
        CurrentTabName = tabName;
        CurrentPage = 0;
        UpdatePageItems();
    }

    public void NextPage()
    {
        if (CurrentPage < TotalPages - 1)
        {
            CurrentPage++;
            UpdatePageItems();
        }
    }

    public void PreviousPage()
    {
        if (CurrentPage > 0)
        {
            CurrentPage--;
            UpdatePageItems();
        }
    }

    private void UpdatePageItems()
    {
        CurrentPageItems.Clear();
        var tab = Tabs.Find(t => t.Name == CurrentTabName);
        if (tab == null) return;

        TotalPages = (int)Math.Ceiling((double)tab.Items.Count / ItemsPerPage);
        if (TotalPages == 0) TotalPages = 1;

        var pageItems = tab.Items.Skip(CurrentPage * ItemsPerPage).Take(ItemsPerPage);
        foreach (var item in pageItems)
            CurrentPageItems.Add(item);
    }

    public void ResetAllSettings()
    {
        var defaults = new EyeCtrlSettings();
        CopySettings(defaults.Cursor, _settings.Cursor);
        CopySettings(defaults.Dwell, _settings.Dwell);
        CopySettings(defaults.ManualSelection, _settings.ManualSelection);
        CopySettings(defaults.ActionMenu, _settings.ActionMenu);
        CopySettings(defaults.Keyboard, _settings.Keyboard);
        CopySettings(defaults.Toolbar, _settings.Toolbar);
        CopySettings(defaults.Theme, _settings.Theme);
        CopySettings(defaults.Backdrop, _settings.Backdrop);
        CopySettings(defaults.General, _settings.General);
        CopySettings(defaults.Calibration, _settings.Calibration);
        CopySettings(defaults.AutoSelection, _settings.AutoSelection);
        CopySettings(defaults.ExternalDwell, _settings.ExternalDwell);
        CopySettings(defaults.Scroll, _settings.Scroll);
        CopySettings(defaults.EyeCare, _settings.EyeCare);
        CopySettings(defaults.Drag, _settings.Drag);
        _settings.SelectionMode = defaults.SelectionMode;
        _settings.ViewPosition = defaults.ViewPosition;

        // Rebuild tabs to reflect new values
        Tabs.Clear();
        BuildTabs();
        if (!string.IsNullOrEmpty(CurrentTabName))
            ShowTab(CurrentTabName);
    }

    private static void CopySettings<T>(T source, T target) where T : class
    {
        foreach (var prop in typeof(T).GetProperties())
        {
            if (prop.CanRead && prop.CanWrite)
                prop.SetValue(target, prop.GetValue(source));
        }
    }

    public void SaveSettings()
    {
        _settingsService.SaveDebounced(_settings);
        _messenger.Send(new SettingsChangedMessage());
    }

    public void Receive(CalibrationCompletedMessage message)
    {
        _calStatusItem!.DisplayText = message.Success
            ? message.MeanErrorPixels is double px
                ? $"Calibrated — mean error {px.ToString("F1", CultureInfo.InvariantCulture)} px"
                : "Calibrated"
            : "Calibration failed";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
