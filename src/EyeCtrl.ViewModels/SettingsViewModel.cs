namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const int ItemsPerPage = 6;

    private readonly EyeCtrlSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly IMessenger _messenger;
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
        BuildTabs();
    }

    private void BuildTabs()
    {
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
        }));

        Tabs.Add(new SettingsTabViewModel("ManualSelection", "\uD83D\uDD0D", new List<SettingItemViewModel>
        {
            new ToggleSettingViewModel("ManualSelectionEnabled", "ManualSelection Enabled", z.Enabled, v => z.Enabled = v),
            new SliderSettingViewModel("ManualSelectionRadius", "Circle Radius (px)", z.CircleRadiusPixels, 50, 500, 10, v => z.CircleRadiusPixels = (int)v),
            new SliderSettingViewModel("magnification", "Magnification Level", z.MagnificationLevel, 1.5, 5, 0.5, v => z.MagnificationLevel = v),
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

        Tabs.Add(new SettingsTabViewModel("General", "\u2699", new List<SettingItemViewModel>
        {
            new ToggleSettingViewModel("startOnBoot", "Start on Boot", g.StartOnBoot, v => g.StartOnBoot = v),
            new ToggleSettingViewModel("startPaused", "Start Paused", g.StartPaused, v => g.StartPaused = v),
            new ComboSettingViewModel("themeMode", "Theme", th.Mode, new[] { "System", "Light", "Dark" }, v => th.Mode = v),
            new SliderSettingViewModel("overlayOpacity", "Overlay Opacity", th.OverlayOpacity, 0.1, 1.0, 0.1, v => th.OverlayOpacity = v),
            new ToggleSettingViewModel("themeHighContrast", "High Contrast", th.HighContrast, v => th.HighContrast = v),
        }));

        Tabs.Add(new SettingsTabViewModel("Calibration", "\u2316", new List<SettingItemViewModel>
        {
            new SliderSettingViewModel("calPointCount", "Point Count", cal.PointCount, 3, 16, 1, v => cal.PointCount = (int)v),
            new SliderSettingViewModel("calSamples", "Samples per Point", cal.SamplesPerPoint, 5, 100, 5, v => cal.SamplesPerPoint = (int)v),
            new SliderSettingViewModel("calHoldMs", "Target Hold (ms)", cal.TargetHoldMs, 200, 3000, 100, v => cal.TargetHoldMs = (int)v),
            new ActionButtonSettingItem("calCalibrate", "Calibrate Now",
                () => WeakReferenceMessenger.Default.Send(new CalibrationRequestedMessage())),
            new ActionButtonSettingItem("calClear", "Clear Calibration",
                () => WeakReferenceMessenger.Default.Send(new CalibrationClearRequestedMessage())),
            new ReadOnlyLabelSettingItem("calStatus", "Last Calibration", "No calibration data"),
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
        CopySettings(defaults.General, _settings.General);
        CopySettings(defaults.Calibration, _settings.Calibration);

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

    public void Dispose()
    {
        if (_disposed)
            return;

        // SettingsViewModel doesn't register message handlers, so no need to unregister
        // However, we implement IDisposable for consistency and future-proofing
        _disposed = true;
    }
}
