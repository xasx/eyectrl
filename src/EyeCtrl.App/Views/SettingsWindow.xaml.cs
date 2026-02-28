namespace EyeCtrl.App.Views;

using System.Windows;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public partial class SettingsWindow : Window
{
    private readonly EyeCtrlSettings _settings;
    private readonly SettingsService _settingsService;

    public event Action<EyeCtrlSettings>? SettingsSaved;

    public SettingsWindow(EyeCtrlSettings settings, SettingsService settingsService)
    {
        InitializeComponent();

        _settings = settings;
        _settingsService = settingsService;

        InitializeComboBoxes();
        LoadSettingsToUI();
    }

    private void InitializeComboBoxes()
    {
        FilterModeCombo.ItemsSource = Enum.GetValues<GazeFilterMode>();
        LayoutCombo.ItemsSource = new[] { "QWERTY", "QWERTZ", "AZERTY" };
        LanguageCombo.ItemsSource = new[] { "en" };
        ScreenEdgeCombo.ItemsSource = new[] { "Bottom", "Top" };
        DockedEdgeCombo.ItemsSource = new[] { "Top", "Bottom", "Left", "Right" };
        ThemeModeCombo.ItemsSource = new[] { "System", "Light", "Dark" };
    }

    private void LoadSettingsToUI()
    {
        // Cursor
        FilterModeCombo.SelectedItem = _settings.Cursor.FilterMode;
        SmoothingSlider.Value = _settings.Cursor.SmoothingFactor;
        SnapRadiusSlider.Value = _settings.Cursor.SnapRadiusPixels;
        CircleColorBox.Text = _settings.Cursor.CircleColor;
        CircleOpacitySlider.Value = _settings.Cursor.CircleOpacity;
        CircleThicknessSlider.Value = _settings.Cursor.CircleThickness;
        HighContrastCheck.IsChecked = _settings.Cursor.CircleHighContrast;

        // Dwell
        FixationDetectionSlider.Value = _settings.Dwell.FixationDetectionMs;
        ActivationDwellSlider.Value = _settings.Dwell.ActivationButtonDwellMs;
        ActionMenuDwellSlider.Value = _settings.Dwell.ActionMenuDwellMs;
        ZoomActionDwellSlider.Value = _settings.Dwell.ZoomActionMenuDwellMs;
        ToolbarDwellSlider.Value = _settings.Dwell.ToolbarButtonDwellMs;
        KeyboardKeyDwellSlider.Value = _settings.Dwell.KeyboardKeyDwellMs;
        PredictionDwellSlider.Value = _settings.Dwell.KeyboardPredictionDwellMs;
        FixationRadiusSlider.Value = _settings.Dwell.FixationRadiusPixels;

        // Zoom
        ZoomEnabledCheck.IsChecked = _settings.Zoom.Enabled;
        ZoomRadiusSlider.Value = _settings.Zoom.CircleRadiusPixels;
        MagnificationSlider.Value = _settings.Zoom.MagnificationLevel;

        // Keyboard
        LayoutCombo.SelectedItem = _settings.Keyboard.ActiveLayout;
        KeySizeSlider.Value = _settings.Keyboard.KeySizeMultiplier;
        PredictionEnabledCheck.IsChecked = _settings.Keyboard.PredictionEnabled;
        LanguageCombo.SelectedItem = _settings.Keyboard.LanguagePack;
        ScreenEdgeCombo.SelectedItem = _settings.Keyboard.ScreenEdge;

        // Toolbar
        ToolbarDockedCheck.IsChecked = _settings.Toolbar.IsDocked;
        DockedEdgeCombo.SelectedItem = _settings.Toolbar.DockedEdge;
        ToolbarSizeSlider.Value = _settings.Toolbar.Size;

        // Theme
        ThemeModeCombo.SelectedItem = _settings.Theme.Mode;
        OverlayOpacitySlider.Value = _settings.Theme.OverlayOpacity;
        ThemeHighContrastCheck.IsChecked = _settings.Theme.HighContrast;

        // General
        StartOnBootCheck.IsChecked = _settings.General.StartOnBoot;
        StartPausedCheck.IsChecked = _settings.General.StartPaused;
    }

    private void SaveUIToSettings()
    {
        // Cursor
        if (FilterModeCombo.SelectedItem is GazeFilterMode filterMode)
            _settings.Cursor.FilterMode = filterMode;
        _settings.Cursor.SmoothingFactor = SmoothingSlider.Value;
        _settings.Cursor.SnapRadiusPixels = (int)SnapRadiusSlider.Value;
        _settings.Cursor.CircleColor = CircleColorBox.Text;
        _settings.Cursor.CircleOpacity = CircleOpacitySlider.Value;
        _settings.Cursor.CircleThickness = CircleThicknessSlider.Value;
        _settings.Cursor.CircleHighContrast = HighContrastCheck.IsChecked == true;

        // Dwell
        _settings.Dwell.FixationDetectionMs = (int)FixationDetectionSlider.Value;
        _settings.Dwell.ActivationButtonDwellMs = (int)ActivationDwellSlider.Value;
        _settings.Dwell.ActionMenuDwellMs = (int)ActionMenuDwellSlider.Value;
        _settings.Dwell.ZoomActionMenuDwellMs = (int)ZoomActionDwellSlider.Value;
        _settings.Dwell.ToolbarButtonDwellMs = (int)ToolbarDwellSlider.Value;
        _settings.Dwell.KeyboardKeyDwellMs = (int)KeyboardKeyDwellSlider.Value;
        _settings.Dwell.KeyboardPredictionDwellMs = (int)PredictionDwellSlider.Value;
        _settings.Dwell.FixationRadiusPixels = FixationRadiusSlider.Value;

        // Zoom
        _settings.Zoom.Enabled = ZoomEnabledCheck.IsChecked == true;
        _settings.Zoom.CircleRadiusPixels = (int)ZoomRadiusSlider.Value;
        _settings.Zoom.MagnificationLevel = MagnificationSlider.Value;

        // Keyboard
        _settings.Keyboard.ActiveLayout = LayoutCombo.SelectedItem as string ?? "QWERTY";
        _settings.Keyboard.KeySizeMultiplier = KeySizeSlider.Value;
        _settings.Keyboard.PredictionEnabled = PredictionEnabledCheck.IsChecked == true;
        _settings.Keyboard.LanguagePack = LanguageCombo.SelectedItem as string ?? "en";
        _settings.Keyboard.ScreenEdge = ScreenEdgeCombo.SelectedItem as string ?? "Bottom";

        // Toolbar
        _settings.Toolbar.IsDocked = ToolbarDockedCheck.IsChecked == true;
        _settings.Toolbar.DockedEdge = DockedEdgeCombo.SelectedItem as string ?? "Top";
        _settings.Toolbar.Size = ToolbarSizeSlider.Value;

        // Theme
        _settings.Theme.Mode = ThemeModeCombo.SelectedItem as string ?? "System";
        _settings.Theme.OverlayOpacity = OverlayOpacitySlider.Value;
        _settings.Theme.HighContrast = ThemeHighContrastCheck.IsChecked == true;

        // General
        _settings.General.StartOnBoot = StartOnBootCheck.IsChecked == true;
        _settings.General.StartPaused = StartPausedCheck.IsChecked == true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveUIToSettings();
        _settingsService.Save(_settings);
        SettingsSaved?.Invoke(_settings);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
