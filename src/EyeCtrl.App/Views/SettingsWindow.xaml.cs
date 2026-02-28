namespace EyeCtrl.App.Views;

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

public partial class SettingsWindow : Window
{
    private const double ControlBtnRadius = 30;
    private static readonly Color BtnBase = Color.FromArgb(200, 60, 60, 80);
    private static readonly Color TabActive = Color.FromArgb(200, 40, 90, 160);
    private static readonly Color TabInactive = Color.FromArgb(200, 50, 50, 50);
    private static readonly Color SaveBase = Color.FromArgb(200, 30, 100, 30);
    private static readonly Color CancelBase = Color.FromArgb(200, 100, 30, 30);

    private readonly EyeCtrlSettings _settings;
    private readonly EyeCtrlSettings _workingCopy;
    private readonly SettingsService _settingsService;
    private readonly int _dwellMs;

    private readonly DwellDetector _tabDwell = new();
    private readonly DwellDetector _actionDwell = new();
    private readonly DwellDetector _controlDwell = new();

    private readonly Dictionary<string, Border> _tabButtons = new();
    private readonly Dictionary<string, Border> _actionButtons = new();
    private readonly Dictionary<string, Border> _controlBorders = new();
    private readonly Dictionary<string, Action> _controlActions = new();

    private string _activeTab = "Cursor";

    private static readonly (string Id, string Label)[] TabDefs =
    [
        ("Cursor", "Cursor"),
        ("Dwell", "Dwell Times"),
        ("Zoom", "Zoom"),
        ("Keyboard", "Keyboard"),
        ("Toolbar", "Toolbar"),
        ("Theme", "Theme"),
        ("General", "General")
    ];

    public event Action<EyeCtrlSettings>? SettingsSaved;

    public SettingsWindow(EyeCtrlSettings settings, SettingsService settingsService)
    {
        InitializeComponent();

        _settings = settings;
        _settingsService = settingsService;
        _dwellMs = settings.Dwell.ToolbarButtonDwellMs;

        // Deep clone for cancel-safe editing
        var json = JsonSerializer.Serialize(settings);
        _workingCopy = JsonSerializer.Deserialize<EyeCtrlSettings>(json)!;

        _tabDwell.TargetActivated += id => Dispatcher.Invoke(() => ShowTab(id));
        _tabDwell.TargetProgress += OnTabProgress;
        _actionDwell.TargetActivated += id => Dispatcher.Invoke(() =>
        {
            if (id == "Save") DoSave();
            else if (id == "Cancel") DoCancel();
        });
        _actionDwell.TargetProgress += OnActionProgress;
        _controlDwell.TargetActivated += id => Dispatcher.Invoke(() =>
        {
            if (_controlActions.TryGetValue(id, out var action)) action();
        });
        _controlDwell.TargetProgress += OnControlProgress;

        BuildTabButtons();
        BuildActionButtons();
        ShowTab("Cursor");
    }

    // ────── Tab buttons ──────

    private void BuildTabButtons()
    {
        foreach (var (id, label) in TabDefs)
        {
            var border = MakeLargeButton(label, TabInactive, 52);
            border.MouseLeftButtonDown += (_, _) => ShowTab(id);
            _tabButtons[id] = border;
            TabButtonPanel.Children.Add(border);
            _tabDwell.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), 70, _dwellMs));
        }
    }

    private void BuildActionButtons()
    {
        foreach (var (id, label, bg) in new[] { ("Save", "Save", SaveBase), ("Cancel", "Cancel", CancelBase) })
        {
            var border = MakeLargeButton(label, bg, 48);
            border.Width = 130;
            border.MouseLeftButtonDown += (_, _) =>
            {
                if (id == "Save") DoSave();
                else DoCancel();
            };
            _actionButtons[id] = border;
            ActionButtonPanel.Children.Add(border);
            _actionDwell.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), 50, _dwellMs));
        }
    }

    private static Border MakeLargeButton(string text, Color bg, double height) => new()
    {
        Height = height,
        Margin = new Thickness(3),
        Padding = new Thickness(12, 0, 12, 0),
        CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(bg),
        Cursor = System.Windows.Input.Cursors.Hand,
        Child = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    // ────── Tab switching ──────

    private void ShowTab(string tabId)
    {
        _activeTab = tabId;
        foreach (var (id, border) in _tabButtons)
            border.Background = new SolidColorBrush(id == tabId ? TabActive : TabInactive);

        RebuildContent();
    }

    private void RebuildContent()
    {
        _controlDwell.ClearTargets();
        _controlBorders.Clear();
        _controlActions.Clear();
        ContentPanel.Children.Clear();

        switch (_activeTab)
        {
            case "Cursor": BuildCursorContent(); break;
            case "Dwell": BuildDwellContent(); break;
            case "Zoom": BuildZoomContent(); break;
            case "Keyboard": BuildKeyboardContent(); break;
            case "Toolbar": BuildToolbarContent(); break;
            case "Theme": BuildThemeContent(); break;
            case "General": BuildGeneralContent(); break;
        }
    }

    // ────── Tab content builders ──────

    private void BuildCursorContent()
    {
        AddCombo("c_filter", "Filter Mode",
            Enum.GetNames<GazeFilterMode>(),
            () => _workingCopy.Cursor.FilterMode.ToString(),
            v => _workingCopy.Cursor.FilterMode = Enum.Parse<GazeFilterMode>(v));

        AddSlider("c_smooth", "Smoothing Factor", 0, 1, 0.1, 1,
            () => _workingCopy.Cursor.SmoothingFactor,
            v => _workingCopy.Cursor.SmoothingFactor = v);

        AddSlider("c_snap", "Snap Radius (px)", 10, 100, 5, 0,
            () => _workingCopy.Cursor.SnapRadiusPixels,
            v => _workingCopy.Cursor.SnapRadiusPixels = (int)v);

        AddColorPicker("c_color", "Circle Color",
            () => _workingCopy.Cursor.CircleColor,
            v => _workingCopy.Cursor.CircleColor = v);

        AddSlider("c_opacity", "Circle Opacity", 0.1, 1, 0.1, 1,
            () => _workingCopy.Cursor.CircleOpacity,
            v => _workingCopy.Cursor.CircleOpacity = v);

        AddSlider("c_thick", "Circle Thickness", 1, 6, 0.5, 1,
            () => _workingCopy.Cursor.CircleThickness,
            v => _workingCopy.Cursor.CircleThickness = v);

        AddToggle("c_hc", "High Contrast Mode",
            () => _workingCopy.Cursor.CircleHighContrast,
            v => _workingCopy.Cursor.CircleHighContrast = v);
    }

    private void BuildDwellContent()
    {
        AddSlider("d_fix", "Fixation Detection (ms)", 50, 500, 25, 0,
            () => _workingCopy.Dwell.FixationDetectionMs,
            v => _workingCopy.Dwell.FixationDetectionMs = (int)v);

        AddSlider("d_act", "Activation Button (ms)", 200, 1500, 50, 0,
            () => _workingCopy.Dwell.ActivationButtonDwellMs,
            v => _workingCopy.Dwell.ActivationButtonDwellMs = (int)v);

        AddSlider("d_menu", "Action Menu (ms)", 200, 1500, 50, 0,
            () => _workingCopy.Dwell.ActionMenuDwellMs,
            v => _workingCopy.Dwell.ActionMenuDwellMs = (int)v);

        AddSlider("d_zoom", "Zoom Action Menu (ms)", 200, 1500, 50, 0,
            () => _workingCopy.Dwell.ZoomActionMenuDwellMs,
            v => _workingCopy.Dwell.ZoomActionMenuDwellMs = (int)v);

        AddSlider("d_tool", "Toolbar / Settings (ms)", 200, 1500, 50, 0,
            () => _workingCopy.Dwell.ToolbarButtonDwellMs,
            v => _workingCopy.Dwell.ToolbarButtonDwellMs = (int)v);

        AddSlider("d_key", "Keyboard Key (ms)", 100, 1000, 25, 0,
            () => _workingCopy.Dwell.KeyboardKeyDwellMs,
            v => _workingCopy.Dwell.KeyboardKeyDwellMs = (int)v);

        AddSlider("d_pred", "Prediction (ms)", 100, 1000, 25, 0,
            () => _workingCopy.Dwell.KeyboardPredictionDwellMs,
            v => _workingCopy.Dwell.KeyboardPredictionDwellMs = (int)v);

        AddSlider("d_rad", "Fixation Radius (px)", 20, 120, 5, 0,
            () => _workingCopy.Dwell.FixationRadiusPixels,
            v => _workingCopy.Dwell.FixationRadiusPixels = v);
    }

    private void BuildZoomContent()
    {
        AddToggle("z_en", "Enable Zoom Mode",
            () => _workingCopy.Zoom.Enabled,
            v => _workingCopy.Zoom.Enabled = v);

        AddSlider("z_rad", "Circle Radius (px)", 50, 300, 10, 0,
            () => _workingCopy.Zoom.CircleRadiusPixels,
            v => _workingCopy.Zoom.CircleRadiusPixels = (int)v);

        AddSlider("z_mag", "Magnification Level", 1.5, 5, 0.5, 1,
            () => _workingCopy.Zoom.MagnificationLevel,
            v => _workingCopy.Zoom.MagnificationLevel = v);
    }

    private void BuildKeyboardContent()
    {
        AddCombo("k_layout", "Active Layout",
            ["QWERTY", "QWERTZ", "AZERTY"],
            () => _workingCopy.Keyboard.ActiveLayout,
            v => _workingCopy.Keyboard.ActiveLayout = v);

        AddSlider("k_size", "Key Size Multiplier", 0.5, 2.0, 0.1, 1,
            () => _workingCopy.Keyboard.KeySizeMultiplier,
            v => _workingCopy.Keyboard.KeySizeMultiplier = v);

        AddToggle("k_pred", "Word Prediction",
            () => _workingCopy.Keyboard.PredictionEnabled,
            v => _workingCopy.Keyboard.PredictionEnabled = v);

        AddCombo("k_lang", "Language Pack", ["en"],
            () => _workingCopy.Keyboard.LanguagePack,
            v => _workingCopy.Keyboard.LanguagePack = v);

        AddCombo("k_edge", "Screen Edge", ["Bottom", "Top"],
            () => _workingCopy.Keyboard.ScreenEdge,
            v => _workingCopy.Keyboard.ScreenEdge = v);
    }

    private void BuildToolbarContent()
    {
        AddToggle("t_dock", "Dock to Screen Edge",
            () => _workingCopy.Toolbar.IsDocked,
            v => _workingCopy.Toolbar.IsDocked = v);

        AddCombo("t_edge", "Docked Edge", ["Top", "Bottom", "Left", "Right"],
            () => _workingCopy.Toolbar.DockedEdge,
            v => _workingCopy.Toolbar.DockedEdge = v);

        AddSlider("t_size", "Button Size", 32, 80, 4, 0,
            () => _workingCopy.Toolbar.Size,
            v => _workingCopy.Toolbar.Size = v);
    }

    private void BuildThemeContent()
    {
        AddCombo("th_mode", "Theme Mode", ["System", "Light", "Dark"],
            () => _workingCopy.Theme.Mode,
            v => _workingCopy.Theme.Mode = v);

        AddSlider("th_op", "Overlay Opacity", 0.3, 1.0, 0.1, 1,
            () => _workingCopy.Theme.OverlayOpacity,
            v => _workingCopy.Theme.OverlayOpacity = v);

        AddToggle("th_hc", "High Contrast",
            () => _workingCopy.Theme.HighContrast,
            v => _workingCopy.Theme.HighContrast = v);
    }

    private void BuildGeneralContent()
    {
        AddToggle("g_boot", "Start on Windows Boot",
            () => _workingCopy.General.StartOnBoot,
            v => _workingCopy.General.StartOnBoot = v);

        AddToggle("g_pause", "Start Paused",
            () => _workingCopy.General.StartPaused,
            v => _workingCopy.General.StartPaused = v);
    }

    // ────── Generic dwellable control builders ──────

    private void AddSlider(string id, string label, double min, double max, double step,
        int decimals, Func<double> get, Action<double> set)
    {
        AddLabel(label);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };

        var valueText = new TextBlock
        {
            Text = FormatVal(get(), decimals),
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Width = 100,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var minus = MakeControlButton(id + "_dn", "\u2212");
        var plus = MakeControlButton(id + "_up", "+");

        _controlActions[id + "_dn"] = () =>
        {
            var v = Math.Round(Math.Max(min, get() - step), decimals);
            set(v);
            valueText.Text = FormatVal(v, decimals);
        };
        _controlActions[id + "_up"] = () =>
        {
            var v = Math.Round(Math.Min(max, get() + step), decimals);
            set(v);
            valueText.Text = FormatVal(v, decimals);
        };

        row.Children.Add(minus);
        row.Children.Add(valueText);
        row.Children.Add(plus);
        ContentPanel.Children.Add(row);
    }

    private void AddCombo(string id, string label, string[] options,
        Func<string> get, Action<string> set)
    {
        AddLabel(label);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };

        var valueText = new TextBlock
        {
            Text = get(),
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Width = 180,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var prev = MakeControlButton(id + "_prev", "\u25C0");
        var next = MakeControlButton(id + "_next", "\u25B6");

        _controlActions[id + "_prev"] = () =>
        {
            var idx = Array.IndexOf(options, get());
            idx = (idx - 1 + options.Length) % options.Length;
            set(options[idx]);
            valueText.Text = options[idx];
        };
        _controlActions[id + "_next"] = () =>
        {
            var idx = Array.IndexOf(options, get());
            idx = (idx + 1) % options.Length;
            set(options[idx]);
            valueText.Text = options[idx];
        };

        row.Children.Add(prev);
        row.Children.Add(valueText);
        row.Children.Add(next);
        ContentPanel.Children.Add(row);
    }

    private void AddToggle(string id, string label, Func<bool> get, Action<bool> set)
    {
        var border = new Border
        {
            Height = 48,
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(16, 0, 16, 0),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var text = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };

        border.Child = text;

        void UpdateVisual()
        {
            var on = get();
            text.Text = (on ? "\u2611 " : "\u2610 ") + label;
            border.Background = new SolidColorBrush(on
                ? Color.FromArgb(200, 30, 100, 30)
                : Color.FromArgb(200, 80, 40, 40));
        }

        UpdateVisual();

        _controlActions[id] = () =>
        {
            set(!get());
            UpdateVisual();
        };

        border.MouseLeftButtonDown += (_, _) => _controlActions[id]();
        _controlBorders[id] = border;
        _controlDwell.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), ControlBtnRadius, _dwellMs));
        ContentPanel.Children.Add(border);
    }

    private static readonly (string Hex, string Name)[] ColorPresets =
    [
        ("#00FF00", "Green"), ("#0088FF", "Blue"), ("#FF0000", "Red"),
        ("#FFFF00", "Yellow"), ("#00FFFF", "Cyan"), ("#FF00FF", "Magenta"),
        ("#FFFFFF", "White"), ("#FF8800", "Orange")
    ];

    private void AddColorPicker(string id, string label, Func<string> get, Action<string> set)
    {
        AddLabel(label);

        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        Border? selectedBorder = null;

        for (int i = 0; i < ColorPresets.Length; i++)
        {
            var (hex, name) = ColorPresets[i];
            var swatchId = $"{id}_{i}";
            var color = (Color)ColorConverter.ConvertFromString(hex);

            var swatch = new Border
            {
                Width = 52,
                Height = 44,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(3),
                BorderBrush = hex.Equals(get(), StringComparison.OrdinalIgnoreCase)
                    ? Brushes.White : Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = name
            };

            if (hex.Equals(get(), StringComparison.OrdinalIgnoreCase))
                selectedBorder = swatch;

            var capturedHex = hex;
            var capturedSwatch = swatch;
            _controlActions[swatchId] = () =>
            {
                set(capturedHex);
                // Clear all selections
                foreach (UIElement child in row.Children)
                    if (child is Border b) b.BorderBrush = Brushes.Transparent;
                capturedSwatch.BorderBrush = Brushes.White;
            };

            swatch.MouseLeftButtonDown += (_, _) => _controlActions[swatchId]();
            _controlBorders[swatchId] = swatch;
            _controlDwell.AddTarget(new DwellTarget(swatchId, new ScreenPoint(0, 0), 22, _dwellMs));
            row.Children.Add(swatch);
        }

        ContentPanel.Children.Add(row);
    }

    // ────── Shared helpers ──────

    private void AddLabel(string text)
    {
        ContentPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            FontSize = 14,
            Margin = new Thickness(0, 6, 0, 2)
        });
    }

    private Border MakeControlButton(string id, string text)
    {
        var btn = new Border
        {
            Width = 56,
            Height = 48,
            Margin = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(BtnBase),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        btn.MouseLeftButtonDown += (_, _) =>
        {
            if (_controlActions.TryGetValue(id, out var action)) action();
        };

        _controlBorders[id] = btn;
        _controlDwell.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), ControlBtnRadius, _dwellMs));
        return btn;
    }

    private static string FormatVal(double v, int decimals) =>
        decimals == 0 ? ((int)v).ToString() : v.ToString($"F{decimals}");

    // ────── Gaze update ──────

    public void UpdateGaze(ScreenPoint screenPoint, long timestampMicroseconds)
    {
        UpdatePositions(_tabButtons, _tabDwell, 70);
        UpdatePositions(_actionButtons, _actionDwell, 50);
        UpdatePositions(_controlBorders, _controlDwell, ControlBtnRadius);

        _tabDwell.Update(screenPoint, timestampMicroseconds);
        _actionDwell.Update(screenPoint, timestampMicroseconds);
        _controlDwell.Update(screenPoint, timestampMicroseconds);
    }

    private void UpdatePositions(Dictionary<string, Border> borders, DwellDetector detector, double radius)
    {
        foreach (var (id, border) in borders)
        {
            try
            {
                var pos = border.PointToScreen(new Point(border.ActualWidth / 2, border.ActualHeight / 2));
                detector.RemoveTarget(id);
                detector.AddTarget(new DwellTarget(id, new ScreenPoint(pos.X, pos.Y), radius, _dwellMs));
            }
            catch
            {
                // Window not yet rendered
            }
        }
    }

    // ────── Dwell visual feedback ──────

    private void OnTabProgress(string targetId, double progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_tabButtons.TryGetValue(targetId, out var border)) return;
            if (targetId == _activeTab) { border.Background = new SolidColorBrush(TabActive); return; }
            border.Background = new SolidColorBrush(Blend(TabInactive, TabActive, progress));
        });
    }

    private void OnActionProgress(string targetId, double progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_actionButtons.TryGetValue(targetId, out var border)) return;
            var baseC = targetId == "Save" ? SaveBase : CancelBase;
            border.Background = new SolidColorBrush(Blend(baseC, Colors.White, progress * 0.4));
        });
    }

    private void OnControlProgress(string targetId, double progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_controlBorders.TryGetValue(targetId, out var border)) return;
            var highlight = Color.FromArgb(200, 0, 180, 0);
            border.Background = new SolidColorBrush(Blend(BtnBase, highlight, progress));
        });
    }

    private static Color Blend(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(from.A * (1 - t) + to.A * t),
            (byte)(from.R * (1 - t) + to.R * t),
            (byte)(from.G * (1 - t) + to.G * t),
            (byte)(from.B * (1 - t) + to.B * t));
    }

    // ────── Save / Cancel ──────

    private void DoSave()
    {
        // Copy working values back to the live settings
        _settings.Cursor.FilterMode = _workingCopy.Cursor.FilterMode;
        _settings.Cursor.SmoothingFactor = _workingCopy.Cursor.SmoothingFactor;
        _settings.Cursor.SnapRadiusPixels = _workingCopy.Cursor.SnapRadiusPixels;
        _settings.Cursor.CircleColor = _workingCopy.Cursor.CircleColor;
        _settings.Cursor.CircleOpacity = _workingCopy.Cursor.CircleOpacity;
        _settings.Cursor.CircleThickness = _workingCopy.Cursor.CircleThickness;
        _settings.Cursor.CircleHighContrast = _workingCopy.Cursor.CircleHighContrast;

        _settings.Dwell.FixationDetectionMs = _workingCopy.Dwell.FixationDetectionMs;
        _settings.Dwell.ActivationButtonDwellMs = _workingCopy.Dwell.ActivationButtonDwellMs;
        _settings.Dwell.ActionMenuDwellMs = _workingCopy.Dwell.ActionMenuDwellMs;
        _settings.Dwell.ZoomActionMenuDwellMs = _workingCopy.Dwell.ZoomActionMenuDwellMs;
        _settings.Dwell.ToolbarButtonDwellMs = _workingCopy.Dwell.ToolbarButtonDwellMs;
        _settings.Dwell.KeyboardKeyDwellMs = _workingCopy.Dwell.KeyboardKeyDwellMs;
        _settings.Dwell.KeyboardPredictionDwellMs = _workingCopy.Dwell.KeyboardPredictionDwellMs;
        _settings.Dwell.FixationRadiusPixels = _workingCopy.Dwell.FixationRadiusPixels;

        _settings.Zoom.Enabled = _workingCopy.Zoom.Enabled;
        _settings.Zoom.CircleRadiusPixels = _workingCopy.Zoom.CircleRadiusPixels;
        _settings.Zoom.MagnificationLevel = _workingCopy.Zoom.MagnificationLevel;

        _settings.Keyboard.ActiveLayout = _workingCopy.Keyboard.ActiveLayout;
        _settings.Keyboard.KeySizeMultiplier = _workingCopy.Keyboard.KeySizeMultiplier;
        _settings.Keyboard.PredictionEnabled = _workingCopy.Keyboard.PredictionEnabled;
        _settings.Keyboard.LanguagePack = _workingCopy.Keyboard.LanguagePack;
        _settings.Keyboard.ScreenEdge = _workingCopy.Keyboard.ScreenEdge;

        _settings.Toolbar.IsDocked = _workingCopy.Toolbar.IsDocked;
        _settings.Toolbar.DockedEdge = _workingCopy.Toolbar.DockedEdge;
        _settings.Toolbar.Size = _workingCopy.Toolbar.Size;

        _settings.Theme.Mode = _workingCopy.Theme.Mode;
        _settings.Theme.OverlayOpacity = _workingCopy.Theme.OverlayOpacity;
        _settings.Theme.HighContrast = _workingCopy.Theme.HighContrast;

        _settings.General.StartOnBoot = _workingCopy.General.StartOnBoot;
        _settings.General.StartPaused = _workingCopy.General.StartPaused;

        _settingsService.Save(_settings);
        SettingsSaved?.Invoke(_settings);
        Close();
    }

    private void DoCancel() => Close();
}
