namespace EyeCtrl.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.App.Overlays;
using EyeCtrl.ViewModels;

public partial class SettingsWindow : OverlayWindow
{
    // ────── Constants ──────

    private const double ControlBtnRadius = 50;
    private const double TabBtnRadius = 85;
    private const double PagBtnRadius = 60;
    private const double ModalBtnRadius = 70;

    // Window sizing
    private const double WindowSizeRatio = 0.9; // Settings window size as fraction of screen

    // UI dimensions
    private const int TabIconFontSize = 30;
    private const int TabContainerHeight = 100;
    private const int TabSymbolFontSize = 22;
    private const int PaginationCounterFontSize = 24;
    private const int PaginationCounterMinWidth = 80;
    private const int PaginationButtonWidth = 100;
    private const int PaginationButtonHeight = 80;
    private const int PaginationSymbolFontSize = 30;

    private static readonly Color BtnBase = Color.FromArgb(200, 60, 60, 80);
    private static readonly Color ControlHighlight = Color.FromArgb(200, 0, 180, 0);
    private static readonly Color TabActive = Color.FromArgb(200, 40, 90, 160);
    private static readonly Color TabInactive = Color.FromArgb(200, 50, 50, 50);
    private static readonly Color ResetBase = Color.FromArgb(200, 128, 80, 32);
    private static readonly Color ModalYes = Color.FromArgb(200, 30, 100, 30);
    private static readonly Color ModalNo = Color.FromArgb(200, 100, 30, 30);
    private static readonly Color CloseBase = Color.FromArgb(200, 100, 30, 30);

    // ────── State ──────

    private readonly SettingsWindowViewModel _viewModel;
    private readonly EyeCtrlSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly int _dwellMs;

    private readonly Dictionary<string, Border> _tabButtons = new();
    private readonly Dictionary<string, Border> _controlBorders = new();
    private readonly Dictionary<string, Border> _pagButtons = new();
    private readonly Dictionary<string, Border> _modalButtons = new();

    private string _activeTab = "Cursor";
    private int _currentPage;
    private int _totalPages;
    private List<List<Action>> _pages = new();

    public event Action<EyeCtrlSettings>? SettingsChanged;

    private readonly (string Id, string Icon, string Label, Func<List<List<Action>>> PageBuilder)[] _tabDefs;
    private readonly Dictionary<string, Func<List<List<Action>>>> _pageBuilders = new();

    public SettingsWindow(
        SettingsWindowViewModel viewModel,
        EyeCtrlSettings settings,
        SettingsService settingsService)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settings = settings;
        _settingsService = settingsService;
        _dwellMs = settings.Dwell.ToolbarButtonDwellMs;

        // Initialize tab definitions with page builders
        _tabDefs =
        [
            ("Cursor",   "◎", "Cursor",      BuildCursorPages),
            ("Dwell",    "⏱", "Dwell Times", BuildDwellPages),
            ("Zoom",     "⊕", "Zoom",        BuildZoomPages),
            ("Actions",  "⊙", "Action Menu", BuildActionMenuPages),
            ("Keyboard", "⌨", "Keyboard",    BuildKeyboardPages),
            ("Toolbar",  "▣", "Toolbar",     BuildToolbarPages),
            ("General",  "⚙", "General",     BuildGeneralPages)
        ];

        // Build dictionary for O(1) page builder lookup
        foreach (var (id, _, _, pageBuilder) in _tabDefs)
        {
            _pageBuilders[id] = pageBuilder;
        }

        // Set DataContext for data binding
        DataContext = _viewModel;

        // Window sizing
        Width = SystemParameters.PrimaryScreenWidth * WindowSizeRatio;
        Height = SystemParameters.PrimaryScreenHeight * WindowSizeRatio;

        // Wire up property change handlers
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Settings.PropertyChanged += OnSettingsViewModelPropertyChanged;

        // Mouse fallback for close button
        CloseButton.MouseLeftButtonDown += (_, _) => Close();

        // Dispose ViewModel on window close
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Settings.PropertyChanged -= OnSettingsViewModelPropertyChanged;
            _viewModel.Dispose();
        };

        // Build UI
        BuildTabButtons();
        ShowTab("Cursor");
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsWindowViewModel.TabProgress):
                    UpdateTabVisuals();
                    break;
                case nameof(SettingsWindowViewModel.ControlProgress):
                    UpdateControlVisuals();
                    break;
                case nameof(SettingsWindowViewModel.PagProgress):
                    UpdatePaginationVisuals();
                    break;
                case nameof(SettingsWindowViewModel.ModalProgress):
                    UpdateModalVisuals();
                    break;
                case nameof(SettingsWindowViewModel.ActiveTabId):
                    UpdateActiveTabHighlight();
                    break;
                case nameof(SettingsWindowViewModel.IsModalVisible):
                    UpdateModalVisibility();
                    break;
            }
        });
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsViewModel.CurrentTabName):
                    if (_viewModel.Settings.CurrentTabName != _activeTab)
                        ShowTab(_viewModel.Settings.CurrentTabName);
                    break;
                case nameof(SettingsViewModel.CurrentPage):
                    // Sync local page index with ViewModel and re-render content
                    _currentPage = _viewModel.Settings.CurrentPage;
                    RenderCurrentPage();
                    RenderPagination();
                    break;
                case nameof(SettingsViewModel.TotalPages):
                    RenderPagination();
                    break;
            }
        });
    }

    // ────── Sidebar tabs ──────

    private void BuildTabButtons()
    {
        foreach (var (id, icon, label, _) in _tabDefs)
        {
            // Set AutomationId on the label TextBlock — WPF's UIA control view only exposes
            // TextBlock nodes from transparent overlay windows; Border containers are collapsed.
            var iconText = new TextBlock
            {
                Text = icon,
                Foreground = Brushes.White,
                FontSize = 30,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(iconText, $"Settings_Tab_{id}");

            var border = new Border
            {
                Height = 100,
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(TabInactive),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16, 0, 16, 0),
                    Children =
                    {
                        iconText,
                        new TextBlock
                        {
                            Text = label,
                            Foreground = Brushes.White,
                            FontSize = 22,
                            FontWeight = FontWeights.SemiBold,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            };

            // Mouse fallback
            border.MouseLeftButtonDown += (_, _) => ShowTab(id);

            // Store border for visual updates
            _tabButtons[id] = border;
            TabButtonPanel.Children.Add(border);

            // Register with ViewModel for dwell detection
            _viewModel.AddTabTarget(id, DpiHelper.LogicalToPhysicalX(TabBtnRadius), _dwellMs);
        }

        // Register close button with ViewModel
        _viewModel.AddTabTarget("close", DpiHelper.LogicalToPhysicalX(TabBtnRadius), _dwellMs);
    }

    // ────── Tab switching ──────

    private void ShowTab(string tabId)
    {
        _activeTab = tabId;
        _currentPage = 0;

        // Update ViewModel's tab state to synchronize pagination
        _viewModel.Settings.ShowTab(tabId);

        foreach (var (id, border) in _tabButtons)
            border.Background = new SolidColorBrush(id == tabId ? TabActive : TabInactive);

        RebuildContent();
    }

    // ────── Live apply + debounced save ──────

    private void ApplyAndSave()
    {
        SettingsChanged?.Invoke(_settings);
        _settingsService.SaveDebounced(_settings);
    }

    // ────── Pagination ──────

    private void RebuildContent()
    {
        _viewModel.ClearControlTargets();
        _controlBorders.Clear();
        ContentPanel.Children.Clear();

        _pages = BuildPages();
        _totalPages = _pages.Count;
        _currentPage = Math.Clamp(_currentPage, 0, Math.Max(0, _totalPages - 1));

        RenderCurrentPage();
        RenderPagination();
    }

    private void RenderCurrentPage()
    {
        _viewModel.ClearControlTargets();
        _controlBorders.Clear();
        ContentPanel.Children.Clear();

        if (_currentPage < _pages.Count)
        {
            foreach (var builder in _pages[_currentPage])
                builder();
        }
    }

    private void RenderPagination()
    {
        PaginationPanel.Children.Clear();
        _pagButtons.Clear();

        // Use ViewModel's pagination state
        if (_viewModel.Settings.TotalPages <= 1) return;

        var prev = MakePagButton("pag_prev", "◀");
        var indicator = new TextBlock
        {
            Text = $"{_viewModel.Settings.CurrentPage + 1} / {_viewModel.Settings.TotalPages}",
            Foreground = Brushes.White,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 20, 0),
            MinWidth = 80,
            TextAlignment = TextAlignment.Center
        };
        var next = MakePagButton("pag_next", "▶");

        // Disable buttons at boundaries
        if (_viewModel.Settings.CurrentPage <= 0) prev.Opacity = 0.3;
        if (_viewModel.Settings.CurrentPage >= _viewModel.Settings.TotalPages - 1) next.Opacity = 0.3;

        PaginationPanel.Children.Add(prev);
        PaginationPanel.Children.Add(indicator);
        PaginationPanel.Children.Add(next);
    }

    private Border MakePagButton(string id, string text)
    {
        var btn = new Border
        {
            Width = 100,
            Height = 80,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(BtnBase),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        // Mouse fallback - ViewModel handles pagination
        btn.MouseLeftButtonDown += (_, _) =>
        {
            if (id == "pag_prev")
                _viewModel.Settings.PreviousPage();
            else
                _viewModel.Settings.NextPage();
        };

        _pagButtons[id] = btn;

        // Register with ViewModel for dwell detection
        _viewModel.AddPaginationTarget(id, DpiHelper.LogicalToPhysicalX(PagBtnRadius), _dwellMs);

        return btn;
    }

    // ────── Page builders per tab ──────

    private List<List<Action>> BuildPages()
    {
        return _pageBuilders.TryGetValue(_activeTab, out var pageBuilder)
            ? pageBuilder.Invoke()
            : new();
    }

    private List<List<Action>> BuildCursorPages()
    {
        return
        [
            [
                () => AddHorizontalList("c_filter", "Filter Mode",
                    Enum.GetNames<GazeFilterMode>(),
                    () => _settings.Cursor.FilterMode.ToString(),
                    v => { _settings.Cursor.FilterMode = Enum.Parse<GazeFilterMode>(v); ApplyAndSave(); }),
                () => AddSlider("c_smooth", "Smoothing Factor", 0, 1, 0.1, 1,
                    () => _settings.Cursor.SmoothingFactor,
                    v => { _settings.Cursor.SmoothingFactor = v; ApplyAndSave(); }),
                () => AddSlider("c_snap", "Snap Radius (px)", 10, 100, 5, 0,
                    () => _settings.Cursor.SnapRadiusPixels,
                    v => { _settings.Cursor.SnapRadiusPixels = (int)v; ApplyAndSave(); }),
                () => AddColorPicker("c_color", "Circle Color",
                    () => _settings.Cursor.CircleColor,
                    v => { _settings.Cursor.CircleColor = v; ApplyAndSave(); }),
            ],
            [
                () => AddSlider("c_opacity", "Circle Opacity", 0.1, 1, 0.1, 1,
                    () => _settings.Cursor.CircleOpacity,
                    v => { _settings.Cursor.CircleOpacity = v; ApplyAndSave(); }),
                () => AddSlider("c_thick", "Circle Thickness", 1, 6, 0.5, 1,
                    () => _settings.Cursor.CircleThickness,
                    v => { _settings.Cursor.CircleThickness = v; ApplyAndSave(); }),
                () => AddToggle("c_hc", "High Contrast Mode",
                    () => _settings.Cursor.CircleHighContrast,
                    v => { _settings.Cursor.CircleHighContrast = v; ApplyAndSave(); }),
                () => AddResetButton("Reset Cursor Settings", () =>
                    ShowModal("Reset Cursor settings to defaults?", () =>
                    {
                        var defaults = new CursorSettings();
                        _settings.Cursor.FilterMode = defaults.FilterMode;
                        _settings.Cursor.SmoothingFactor = defaults.SmoothingFactor;
                        _settings.Cursor.SnapRadiusPixels = defaults.SnapRadiusPixels;
                        _settings.Cursor.CircleColor = defaults.CircleColor;
                        _settings.Cursor.CircleOpacity = defaults.CircleOpacity;
                        _settings.Cursor.CircleThickness = defaults.CircleThickness;
                        _settings.Cursor.CircleHighContrast = defaults.CircleHighContrast;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildDwellPages()
    {
        return
        [
            [
                () => AddSlider("d_fix", "Fixation Detection (ms)", 50, 500, 25, 0,
                    () => _settings.Dwell.FixationDetectionMs,
                    v => { _settings.Dwell.FixationDetectionMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_act", "Activation Button (ms)", 200, 1500, 50, 0,
                    () => _settings.Dwell.ActivationButtonDwellMs,
                    v => { _settings.Dwell.ActivationButtonDwellMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_menu", "Action Menu (ms)", 200, 1500, 50, 0,
                    () => _settings.Dwell.ActionMenuDwellMs,
                    v => { _settings.Dwell.ActionMenuDwellMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_zoom", "Zoom Action Menu (ms)", 200, 1500, 50, 0,
                    () => _settings.Dwell.ZoomActionMenuDwellMs,
                    v => { _settings.Dwell.ZoomActionMenuDwellMs = (int)v; ApplyAndSave(); }),
            ],
            [
                () => AddSlider("d_tool", "Toolbar / Settings (ms)", 200, 1500, 50, 0,
                    () => _settings.Dwell.ToolbarButtonDwellMs,
                    v => { _settings.Dwell.ToolbarButtonDwellMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_key", "Keyboard Key (ms)", 100, 1000, 25, 0,
                    () => _settings.Dwell.KeyboardKeyDwellMs,
                    v => { _settings.Dwell.KeyboardKeyDwellMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_pred", "Prediction (ms)", 100, 1000, 25, 0,
                    () => _settings.Dwell.KeyboardPredictionDwellMs,
                    v => { _settings.Dwell.KeyboardPredictionDwellMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_rad", "Fixation Radius (px)", 20, 120, 5, 0,
                    () => _settings.Dwell.FixationRadiusPixels,
                    v => { _settings.Dwell.FixationRadiusPixels = v; ApplyAndSave(); }),
                () => AddSlider("d_cooldown", "Dwell Cooldown (ms)", 0, 1000, 50, 0,
                    () => _settings.Dwell.DwellCooldownMs,
                    v => { _settings.Dwell.DwellCooldownMs = (int)v; ApplyAndSave(); }),
                () => AddResetButton("Reset Dwell Settings", () =>
                    ShowModal("Reset Dwell settings to defaults?", () =>
                    {
                        var d = new DwellSettings();
                        _settings.Dwell.FixationDetectionMs = d.FixationDetectionMs;
                        _settings.Dwell.ActivationButtonDwellMs = d.ActivationButtonDwellMs;
                        _settings.Dwell.ActionMenuDwellMs = d.ActionMenuDwellMs;
                        _settings.Dwell.ZoomActionMenuDwellMs = d.ZoomActionMenuDwellMs;
                        _settings.Dwell.ToolbarButtonDwellMs = d.ToolbarButtonDwellMs;
                        _settings.Dwell.KeyboardKeyDwellMs = d.KeyboardKeyDwellMs;
                        _settings.Dwell.KeyboardPredictionDwellMs = d.KeyboardPredictionDwellMs;
                        _settings.Dwell.FixationRadiusPixels = d.FixationRadiusPixels;
                        _settings.Dwell.DwellCooldownMs = d.DwellCooldownMs;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildZoomPages()
    {
        return
        [
            [
                () => AddToggle("z_en", "Enable Zoom Mode",
                    () => _settings.Zoom.Enabled,
                    v => { _settings.Zoom.Enabled = v; ApplyAndSave(); }),
                () => AddHorizontalList("z_pos", "Position",
                    ["At Gaze", "At Center"],
                    () => _settings.Zoom.Position == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { _settings.Zoom.Position = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; ApplyAndSave(); }),
                () => AddSlider("z_rad", "Circle Radius (px)", 50, 300, 10, 0,
                    () => _settings.Zoom.CircleRadiusPixels,
                    v => { _settings.Zoom.CircleRadiusPixels = (int)v; ApplyAndSave(); }),
                () => AddSlider("z_mag", "Magnification Level", 1.5, 5, 0.5, 1,
                    () => _settings.Zoom.MagnificationLevel,
                    v => { _settings.Zoom.MagnificationLevel = v; ApplyAndSave(); }),
                () => AddSlider("z_grace", "Grace Period (ms)", 0, 2000, 50, 0,
                    () => _settings.Zoom.ZoomGracePeriodMs,
                    v => { _settings.Zoom.ZoomGracePeriodMs = (int)v; ApplyAndSave(); }),
                () => AddResetButton("Reset Zoom Settings", () =>
                    ShowModal("Reset Zoom settings to defaults?", () =>
                    {
                        var z = new ZoomSettings();
                        _settings.Zoom.Enabled = z.Enabled;
                        _settings.Zoom.Position = z.Position;
                        _settings.Zoom.CircleRadiusPixels = z.CircleRadiusPixels;
                        _settings.Zoom.MagnificationLevel = z.MagnificationLevel;
                        _settings.Zoom.ZoomGracePeriodMs = z.ZoomGracePeriodMs;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildActionMenuPages()
    {
        return
        [
            [
                () => AddHorizontalList("am_pos", "Position",
                    ["At Gaze", "At Center"],
                    () => _settings.ActionMenu.Position == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { _settings.ActionMenu.Position = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; ApplyAndSave(); }),
                () => AddResetButton("Reset Action Menu Settings", () =>
                    ShowModal("Reset Action Menu settings to defaults?", () =>
                    {
                        var a = new ActionMenuSettings();
                        _settings.ActionMenu.Position = a.Position;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildKeyboardPages()
    {
        return
        [
            [
                () => AddHorizontalList("k_layout", "Active Layout",
                    ["QWERTY", "QWERTZ", "AZERTY"],
                    () => _settings.Keyboard.ActiveLayout,
                    v => { _settings.Keyboard.ActiveLayout = v; ApplyAndSave(); }),
                () => AddSlider("k_size", "Key Size Multiplier", 0.5, 2.0, 0.1, 1,
                    () => _settings.Keyboard.KeySizeMultiplier,
                    v => { _settings.Keyboard.KeySizeMultiplier = v; ApplyAndSave(); }),
                () => AddToggle("k_pred", "Word Prediction",
                    () => _settings.Keyboard.PredictionEnabled,
                    v => { _settings.Keyboard.PredictionEnabled = v; ApplyAndSave(); }),
                () => AddHorizontalList("k_lang", "Dictionary Language",
                    displayOptions: ["English", "French", "Spanish", "German", "Polish"],
                    codeOptions:    ["en",      "fr",     "es",      "de",     "pl"],
                    getCode: () => string.IsNullOrEmpty(_settings.Keyboard.LanguagePack) ? "en"
                                   : _settings.Keyboard.LanguagePack,
                    setCode: v => { _settings.Keyboard.LanguagePack = v; ApplyAndSave(); }),
                () => AddToggle("k_sound", "Click Sound",
                    () => _settings.Keyboard.ClickSoundEnabled,
                    v => { _settings.Keyboard.ClickSoundEnabled = v; ApplyAndSave(); }),
                () => AddHorizontalList("k_edge", "Screen Edge",
                    ["Bottom", "Top"],
                    () => _settings.Keyboard.ScreenEdge,
                    v => { _settings.Keyboard.ScreenEdge = v; ApplyAndSave(); }),
                () => AddResetButton("Reset Keyboard Settings", () =>
                    ShowModal("Reset Keyboard settings to defaults?", () =>
                    {
                        var k = new KeyboardSettings();
                        _settings.Keyboard.ActiveLayout = k.ActiveLayout;
                        _settings.Keyboard.KeySizeMultiplier = k.KeySizeMultiplier;
                        _settings.Keyboard.PredictionEnabled = k.PredictionEnabled;
                        _settings.Keyboard.LanguagePack = "";
                        _settings.Keyboard.ScreenEdge = k.ScreenEdge;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildToolbarPages()
    {
        return
        [
            [
                () => AddToggle("t_dock", "Dock to Screen Edge",
                    () => _settings.Toolbar.IsDocked,
                    v => { _settings.Toolbar.IsDocked = v; ApplyAndSave(); }),
                () => AddHorizontalList("t_edge", "Docked Edge",
                    ["Top", "Bottom", "Left", "Right"],
                    () => _settings.Toolbar.DockedEdge,
                    v => { _settings.Toolbar.DockedEdge = v; ApplyAndSave(); }),
                () => AddSlider("t_size", "Button Size", 32, 120, 4, 0,
                    () => _settings.Toolbar.Size,
                    v => { _settings.Toolbar.Size = v; ApplyAndSave(); }),
                () => AddResetButton("Reset Toolbar Settings", () =>
                    ShowModal("Reset Toolbar settings to defaults?", () =>
                    {
                        var t = new ToolbarSettings();
                        _settings.Toolbar.IsDocked = t.IsDocked;
                        _settings.Toolbar.DockedEdge = t.DockedEdge;
                        _settings.Toolbar.Size = t.Size;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildThemePages()
    {
        return
        [
            [
                () => AddHorizontalList("th_mode", "Theme Mode",
                    ["System", "Light", "Dark"],
                    () => _settings.Theme.Mode,
                    v => { _settings.Theme.Mode = v; ApplyAndSave(); }),
                () => AddSlider("th_op", "Overlay Opacity", 0.3, 1.0, 0.1, 1,
                    () => _settings.Theme.OverlayOpacity,
                    v => { _settings.Theme.OverlayOpacity = v; ApplyAndSave(); }),
                () => AddToggle("th_hc", "High Contrast",
                    () => _settings.Theme.HighContrast,
                    v => { _settings.Theme.HighContrast = v; ApplyAndSave(); }),
                () => AddResetButton("Reset Theme Settings", () =>
                    ShowModal("Reset Theme settings to defaults?", () =>
                    {
                        var th = new ThemeSettings();
                        _settings.Theme.Mode = th.Mode;
                        _settings.Theme.OverlayOpacity = th.OverlayOpacity;
                        _settings.Theme.HighContrast = th.HighContrast;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildGeneralPages()
    {
        return
        [
            [
                () => AddToggle("g_boot", "Start on Windows Boot",
                    () => _settings.General.StartOnBoot,
                    v => { _settings.General.StartOnBoot = v; ApplyAndSave(); }),
                () => AddToggle("g_pause", "Start Paused",
                    () => _settings.General.StartPaused,
                    v => { _settings.General.StartPaused = v; ApplyAndSave(); }),
                () => AddResetButton("Reset General Settings", () =>
                    ShowModal("Reset General settings to defaults?", () =>
                    {
                        var g = new GeneralSettings();
                        _settings.General.StartOnBoot = g.StartOnBoot;
                        _settings.General.StartPaused = g.StartPaused;
                        ApplyAndSave();
                        RebuildContent();
                    })),
                () => AddResetButton("\u26A0 Reset ALL Settings", () =>
                    ShowModal("Reset ALL settings to defaults?\nThis cannot be undone.", () =>
                    {
                        var d = new EyeCtrlSettings();
                        _settings.Cursor.FilterMode = d.Cursor.FilterMode;
                        _settings.Cursor.SmoothingFactor = d.Cursor.SmoothingFactor;
                        _settings.Cursor.SnapRadiusPixels = d.Cursor.SnapRadiusPixels;
                        _settings.Cursor.CircleColor = d.Cursor.CircleColor;
                        _settings.Cursor.CircleOpacity = d.Cursor.CircleOpacity;
                        _settings.Cursor.CircleThickness = d.Cursor.CircleThickness;
                        _settings.Cursor.CircleHighContrast = d.Cursor.CircleHighContrast;
                        _settings.Dwell.FixationDetectionMs = d.Dwell.FixationDetectionMs;
                        _settings.Dwell.ActivationButtonDwellMs = d.Dwell.ActivationButtonDwellMs;
                        _settings.Dwell.ActionMenuDwellMs = d.Dwell.ActionMenuDwellMs;
                        _settings.Dwell.ZoomActionMenuDwellMs = d.Dwell.ZoomActionMenuDwellMs;
                        _settings.Dwell.ToolbarButtonDwellMs = d.Dwell.ToolbarButtonDwellMs;
                        _settings.Dwell.KeyboardKeyDwellMs = d.Dwell.KeyboardKeyDwellMs;
                        _settings.Dwell.KeyboardPredictionDwellMs = d.Dwell.KeyboardPredictionDwellMs;
                        _settings.Dwell.FixationRadiusPixels = d.Dwell.FixationRadiusPixels;
                        _settings.Zoom.Enabled = d.Zoom.Enabled;
                        _settings.Zoom.CircleRadiusPixels = d.Zoom.CircleRadiusPixels;
                        _settings.Zoom.MagnificationLevel = d.Zoom.MagnificationLevel;
                        _settings.Keyboard.ActiveLayout = d.Keyboard.ActiveLayout;
                        _settings.Keyboard.KeySizeMultiplier = d.Keyboard.KeySizeMultiplier;
                        _settings.Keyboard.PredictionEnabled = d.Keyboard.PredictionEnabled;
                        _settings.Keyboard.LanguagePack = "";
                        _settings.Keyboard.ScreenEdge = d.Keyboard.ScreenEdge;
                        _settings.Toolbar.IsDocked = d.Toolbar.IsDocked;
                        _settings.Toolbar.DockedEdge = d.Toolbar.DockedEdge;
                        _settings.Toolbar.Size = d.Toolbar.Size;
                        _settings.Theme.Mode = d.Theme.Mode;
                        _settings.Theme.OverlayOpacity = d.Theme.OverlayOpacity;
                        _settings.Theme.HighContrast = d.Theme.HighContrast;
                        _settings.General.StartOnBoot = d.General.StartOnBoot;
                        _settings.General.StartPaused = d.General.StartPaused;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    // ────── Generic dwellable control builders (scaled up) ──────

    private void AddSlider(string id, string label, double min, double max, double step,
        int decimals, Func<double> get, Action<double> set)
    {
        AddLabel(label);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 12) };

        var valueText = new TextBlock
        {
            Text = FormatVal(get(), decimals),
            Foreground = Brushes.White,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Width = 140,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var minus = MakeControlButton(id + "_dn", "\u2212", () =>
        {
            var v = Math.Round(Math.Max(min, get() - step), decimals);
            set(v);
            valueText.Text = FormatVal(v, decimals);
        });
        var plus = MakeControlButton(id + "_up", "+", () =>
        {
            var v = Math.Round(Math.Min(max, get() + step), decimals);
            set(v);
            valueText.Text = FormatVal(v, decimals);
        });

        row.Children.Add(minus);
        row.Children.Add(valueText);
        row.Children.Add(plus);
        ContentPanel.Children.Add(row);
    }

    private void AddHorizontalList(string idPrefix, string label, string[] options,
        Func<string> get, Action<string> set)
        => AddHorizontalList(idPrefix, label, options, options, get, set);

    private void AddHorizontalList(
        string idPrefix,
        string label,
        string[] displayOptions,
        string[] codeOptions,
        Func<string> getCode,
        Action<string> setCode)
    {
        AddLabel(label);

        var borders = new List<Border>();
        var texts = new List<TextBlock>();

        var wrapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 12)
        };

        void Refresh()
        {
            var active = getCode();
            for (int j = 0; j < borders.Count; j++)
            {
                bool selected = j < codeOptions.Length && codeOptions[j] == active;
                borders[j].Background = new SolidColorBrush(selected
                    ? Color.FromArgb(200, 30, 100, 30)
                    : BtnBase);
                if (j < texts.Count)
                    texts[j].Text = (selected ? "\u25C9 " : "\u25CB ") + displayOptions[j];
            }
        }

        for (int i = 0; i < displayOptions.Length; i++)
        {
            var capturedCode = codeOptions[i];
            var id = $"{idPrefix}_{i}";

            var text = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            System.Windows.Automation.AutomationProperties.SetAutomationId(
                text, $"Settings_HList_{id}");

            var border = new Border
            {
                Height = 70,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(20, 0, 20, 0),
                CornerRadius = new CornerRadius(8),
                MinWidth = 150,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = text
            };

            border.MouseLeftButtonDown += (_, _) =>
            {
                setCode(capturedCode);
                Refresh();
            };

            _controlBorders[id] = border;
            _viewModel.RegisterControlAction(id, () =>
            {
                setCode(capturedCode);
                Refresh();
            });
            _viewModel.AddControlTarget(id, DpiHelper.LogicalToPhysicalX(ControlBtnRadius), _dwellMs);

            borders.Add(border);
            texts.Add(text);
            wrapPanel.Children.Add(border);
        }

        ContentPanel.Children.Add(wrapPanel);
        Refresh();
    }

    private void AddToggle(string id, string label, Func<bool> get, Action<bool> set)
    {
        var border = new Border
        {
            Height = 80,
            Margin = new Thickness(0, 4, 0, 12),
            Padding = new Thickness(20, 0, 20, 0),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var text = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };

        System.Windows.Automation.AutomationProperties.SetAutomationId(text, $"Settings_Toggle_{id}");
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

        border.MouseLeftButtonDown += (_, _) =>
        {
            set(!get());
            UpdateVisual();
        };

        _controlBorders[id] = border;
        _viewModel.RegisterControlAction(id, () =>
        {
            set(!get());
            UpdateVisual();
        });
        _viewModel.AddControlTarget(id, DpiHelper.LogicalToPhysicalX(ControlBtnRadius), _dwellMs);
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

        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 12) };

        for (int i = 0; i < ColorPresets.Length; i++)
        {
            var (hex, name) = ColorPresets[i];
            var swatchId = $"{id}_{i}";
            var color = (Color)ColorConverter.ConvertFromString(hex);

            var swatch = new Border
            {
                Width = 90,
                Height = 75,
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(3),
                BorderBrush = hex.Equals(get(), StringComparison.OrdinalIgnoreCase)
                    ? Brushes.White : Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = name
            };

            var capturedHex = hex;
            var capturedSwatch = swatch;

            swatch.MouseLeftButtonDown += (_, _) =>
            {
                set(capturedHex);
                foreach (UIElement child in row.Children)
                    if (child is Border b) b.BorderBrush = Brushes.Transparent;
                capturedSwatch.BorderBrush = Brushes.White;
            };

            _controlBorders[swatchId] = swatch;
            _viewModel.RegisterControlAction(swatchId, () =>
            {
                set(capturedHex);
                foreach (UIElement child in row.Children)
                    if (child is Border b) b.BorderBrush = Brushes.Transparent;
                capturedSwatch.BorderBrush = Brushes.White;
            });
            _viewModel.AddControlTarget(swatchId, DpiHelper.LogicalToPhysicalX(30), _dwellMs);
            row.Children.Add(swatch);
        }

        ContentPanel.Children.Add(row);
    }

    private void AddResetButton(string label, Action onClick)
    {
        var id = $"reset_{label.GetHashCode():X}";
        var border = new Border
        {
            Width = 300,
            Height = 80,
            Margin = new Thickness(0, 20, 0, 8),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(ResetBase),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        border.MouseLeftButtonDown += (_, _) => onClick();
        _controlBorders[id] = border;
        _viewModel.RegisterControlAction(id, onClick);
        _viewModel.AddControlTarget(id, DpiHelper.LogicalToPhysicalX(ControlBtnRadius), _dwellMs);
        ContentPanel.Children.Add(border);
    }

    // ────── Shared helpers ──────

    private void AddLabel(string text)
    {
        ContentPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            FontSize = 22,
            Margin = new Thickness(0, 8, 0, 2)
        });
    }

    private Border MakeControlButton(string id, string text, Action action)
    {
        var btn = new Border
        {
            Width = 100,
            Height = 80,
            Margin = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(BtnBase),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        btn.MouseLeftButtonDown += (_, _) => action();
        _controlBorders[id] = btn;
        _viewModel.RegisterControlAction(id, action);
        _viewModel.AddControlTarget(id, DpiHelper.LogicalToPhysicalX(ControlBtnRadius), _dwellMs);
        return btn;
    }

    private static string FormatVal(double v, int decimals) =>
        decimals == 0 ? ((int)v).ToString() : v.ToString($"F{decimals}");

    // ────── Gaze update ──────

    public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds)
    {
        // Update button positions for ViewModel's dwell detection
        // ViewModel handles actual dwell updates via GazeCursorMovedMessage
        UpdateAllTargetPositions();
    }

    private void UpdateAllTargetPositions()
    {
        // Update close button position
        try
        {
            var cPos = CloseButton.PointToScreen(
                new Point(CloseButton.ActualWidth / 2, CloseButton.ActualHeight / 2));
            _viewModel.UpdateTabButtonPosition("close", new PhysicalPoint(cPos.X, cPos.Y));
        }
        catch (InvalidOperationException)
        {
            // Window or button not yet rendered or not in visual tree
        }

        // Update tab button positions
        UpdateButtonPositions(_tabButtons, (id, pos) => _viewModel.UpdateTabButtonPosition(id, pos));

        // Update control button positions
        UpdateButtonPositions(_controlBorders, (id, pos) => _viewModel.UpdateControlPosition(id, pos));

        // Update pagination button positions
        UpdateButtonPositions(_pagButtons, (id, pos) => _viewModel.UpdatePaginationButtonPosition(id, pos));

        // Update modal button positions if modal is visible
        if (_viewModel.IsModalVisible)
        {
            UpdateButtonPositions(_modalButtons, (id, pos) => _viewModel.UpdateModalButtonPosition(id, pos));
        }
    }

    private static void UpdateButtonPositions(
        Dictionary<string, Border> borders,
        Action<string, PhysicalPoint> updateAction)
    {
        foreach (var (id, border) in borders)
        {
            try
            {
                var pos = border.PointToScreen(
                    new Point(border.ActualWidth / 2, border.ActualHeight / 2));
                updateAction(id, new PhysicalPoint(pos.X, pos.Y));
            }
            catch (InvalidOperationException)
            {
                // Border not yet rendered or not in visual tree
            }
        }
    }

    // ────── Dwell visual feedback ──────

    private void UpdateTabVisuals()
    {
        foreach (var (id, progress) in _viewModel.TabProgress)
        {
            if (id == "close")
            {
                CloseButton.Background = new SolidColorBrush(
                    Blend(CloseBase, Colors.White, progress * 0.5));
                continue;
            }

            if (_tabButtons.TryGetValue(id, out var border) && id != _viewModel.ActiveTabId)
            {
                border.Background = new SolidColorBrush(Blend(TabInactive, TabActive, progress));
            }
        }
    }

    private void UpdateControlVisuals()
    {
        foreach (var (id, progress) in _viewModel.ControlProgress)
        {
            if (_controlBorders.TryGetValue(id, out var border))
            {
                border.Background = new SolidColorBrush(Blend(BtnBase, ControlHighlight, progress));
            }
        }
    }

    private void UpdatePaginationVisuals()
    {
        foreach (var (id, progress) in _viewModel.PagProgress)
        {
            if (_pagButtons.TryGetValue(id, out var border))
            {
                border.Background = new SolidColorBrush(Blend(BtnBase, Colors.White, progress * 0.4));
            }
        }
    }

    private void UpdateModalVisuals()
    {
        foreach (var (id, progress) in _viewModel.ModalProgress)
        {
            if (_modalButtons.TryGetValue(id, out var border))
            {
                var baseC = id == "modal_yes" ? ModalYes : ModalNo;
                border.Background = new SolidColorBrush(Blend(baseC, Colors.White, progress * 0.5));
            }
        }
    }

    private void UpdateActiveTabHighlight()
    {
        foreach (var (id, border) in _tabButtons)
        {
            border.Background = new SolidColorBrush(id == _viewModel.ActiveTabId ? TabActive : TabInactive);
        }
    }

    private void UpdateModalVisibility()
    {
        if (_viewModel.IsModalVisible)
        {
            ModalMessage.Text = _viewModel.ModalMessage;
            ModalButtonPanel.Children.Clear();
            _modalButtons.Clear();

            var yesBtn = MakeModalButton("modal_yes", "\u2713 Yes", ModalYes);
            var noBtn = MakeModalButton("modal_no", "\u2715 No", ModalNo);

            ModalButtonPanel.Children.Add(yesBtn);
            ModalButtonPanel.Children.Add(noBtn);

            // Register modal buttons as dwell targets with DPI-scaled radius
            _viewModel.AddModalTarget("modal_yes", DpiHelper.LogicalToPhysicalX(ModalBtnRadius), _dwellMs);
            _viewModel.AddModalTarget("modal_no", DpiHelper.LogicalToPhysicalX(ModalBtnRadius), _dwellMs);

            ModalOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            _modalButtons.Clear();
        }
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

    // ────── Modal confirmation ──────

    private void ShowModal(string message, Action onYes)
    {
        // Delegate to ViewModel
        _viewModel.ShowModal(message, onYes);
    }

    private Border MakeModalButton(string id, string text, Color bg)
    {
        var border = new Border
        {
            Width = 170,
            Height = 80,
            Margin = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(bg),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        // Mouse fallback - ViewModel handles the actual activation
        border.MouseLeftButtonDown += (_, _) =>
        {
            if (id == "modal_yes")
                _viewModel.SimulateModalButtonActivated(id);
            else
                _viewModel.HideModal();
        };

        _modalButtons[id] = border;
        return border;
    }
}
