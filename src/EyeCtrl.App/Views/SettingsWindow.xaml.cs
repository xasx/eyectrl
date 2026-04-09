namespace EyeCtrl.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.App.Controls;
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
    private readonly Dictionary<string, Border> _pagButtons = new();
    private readonly Dictionary<string, Border> _modalButtons = new();

    private string _activeTab = "Cursor";
    private int _currentPage;
    private int _totalPages;
    private List<List<Action>> _pages = new();
    private readonly List<object> _activeItems = new(); // DwellControl | IDwellComposite

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
                    {
                        _activeTab = _viewModel.Settings.CurrentTabName;
                        foreach (var (id, border) in _tabButtons)
                            border.Background = new SolidColorBrush(id == _activeTab ? TabActive : TabInactive);
                        RebuildContent();
                    }
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
            _viewModel.Register(id, DwellCategory.Tab, DpiHelper.LogicalToPhysicalX(TabBtnRadius), _dwellMs, () => { });
        }

        // Register close button with ViewModel
        _viewModel.Register("close", DwellCategory.Tab, DpiHelper.LogicalToPhysicalX(TabBtnRadius), _dwellMs, () => { });
    }

    // ────── Tab switching ──────

    private void ShowTab(string tabId)
    {
        // Update ViewModel's tab state - property changed handler will rebuild content
        _viewModel.Settings.ShowTab(tabId);
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
        _viewModel.ClearCategory(DwellCategory.Control);
        ContentPanel.Children.Clear();

        _pages = BuildPages();
        _totalPages = _pages.Count;
        _currentPage = Math.Clamp(_currentPage, 0, Math.Max(0, _totalPages - 1));

        RenderCurrentPage();
        RenderPagination();
    }

    private void RenderCurrentPage()
    {
        _viewModel.ClearCategory(DwellCategory.Control);
        ContentPanel.Children.Clear();
        _activeItems.Clear();

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
        _viewModel.Register(id, DwellCategory.Pagination, DpiHelper.LogicalToPhysicalX(PagBtnRadius), _dwellMs, () => { });

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
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellComboBox(_viewModel, "c_filter", "Filter Mode",
                    Enum.GetNames<GazeFilterMode>(),
                    () => _settings.Cursor.FilterMode.ToString(),
                    v => { _settings.Cursor.FilterMode = Enum.Parse<GazeFilterMode>(v); ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "c_smooth", "Smoothing Factor",
                    0, 1, 0.1, 1,
                    () => _settings.Cursor.SmoothingFactor,
                    v => { _settings.Cursor.SmoothingFactor = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "c_snap", "Snap Radius (px)",
                    10, 100, 5, 0,
                    () => _settings.Cursor.SnapRadiusPixels,
                    v => { _settings.Cursor.SnapRadiusPixels = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellColorPicker(_viewModel, "c_color", "Circle Color",
                    () => _settings.Cursor.CircleColor,
                    v => { _settings.Cursor.CircleColor = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
            ],
            [
                () => Add(new DwellSlider(_viewModel, "c_opacity", "Circle Opacity",
                    0.1, 1, 0.1, 1,
                    () => _settings.Cursor.CircleOpacity,
                    v => { _settings.Cursor.CircleOpacity = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "c_thick", "Circle Thickness",
                    1, 6, 0.5, 1,
                    () => _settings.Cursor.CircleThickness,
                    v => { _settings.Cursor.CircleThickness = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellToggle(_viewModel, "c_hc", "High Contrast Mode",
                    () => _settings.Cursor.CircleHighContrast,
                    v => { _settings.Cursor.CircleHighContrast = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "c_reset", "Reset Cursor Settings",
                    () => ShowModal("Reset Cursor settings to defaults?", () =>
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
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }

    private List<List<Action>> BuildDwellPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellSlider(_viewModel, "d_fix", "Fixation Detection (ms)",
                    50, 500, 25, 0,
                    () => _settings.Dwell.FixationDetectionMs,
                    v => { _settings.Dwell.FixationDetectionMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_act", "Activation Button (ms)",
                    200, 1500, 50, 0,
                    () => _settings.Dwell.ActivationButtonDwellMs,
                    v => { _settings.Dwell.ActivationButtonDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_menu", "Action Menu (ms)",
                    200, 1500, 50, 0,
                    () => _settings.Dwell.ActionMenuDwellMs,
                    v => { _settings.Dwell.ActionMenuDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_zoom", "Zoom Action Menu (ms)",
                    200, 1500, 50, 0,
                    () => _settings.Dwell.ZoomActionMenuDwellMs,
                    v => { _settings.Dwell.ZoomActionMenuDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
            ],
            [
                () => Add(new DwellSlider(_viewModel, "d_tool", "Toolbar / Settings (ms)",
                    200, 1500, 50, 0,
                    () => _settings.Dwell.ToolbarButtonDwellMs,
                    v => { _settings.Dwell.ToolbarButtonDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_key", "Keyboard Key (ms)",
                    100, 1000, 25, 0,
                    () => _settings.Dwell.KeyboardKeyDwellMs,
                    v => { _settings.Dwell.KeyboardKeyDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_pred", "Prediction (ms)",
                    100, 1000, 25, 0,
                    () => _settings.Dwell.KeyboardPredictionDwellMs,
                    v => { _settings.Dwell.KeyboardPredictionDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_rad", "Fixation Radius (px)",
                    20, 120, 5, 0,
                    () => _settings.Dwell.FixationRadiusPixels,
                    v => { _settings.Dwell.FixationRadiusPixels = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_cooldown", "Dwell Cooldown (ms)",
                    0, 1000, 50, 0,
                    () => _settings.Dwell.DwellCooldownMs,
                    v => { _settings.Dwell.DwellCooldownMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "d_reset", "Reset Dwell Settings",
                    () => ShowModal("Reset Dwell settings to defaults?", () =>
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
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }

    private List<List<Action>> BuildZoomPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellToggle(_viewModel, "z_en", "Enable Zoom Mode",
                    () => _settings.Zoom.Enabled,
                    v => { _settings.Zoom.Enabled = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellComboBox(_viewModel, "z_pos", "Position",
                    ["At Gaze", "At Center"],
                    () => _settings.Zoom.Position == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { _settings.Zoom.Position = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "z_rad", "Circle Radius (px)",
                    50, 300, 10, 0,
                    () => _settings.Zoom.CircleRadiusPixels,
                    v => { _settings.Zoom.CircleRadiusPixels = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "z_mag", "Magnification Level",
                    1.5, 5, 0.5, 1,
                    () => _settings.Zoom.MagnificationLevel,
                    v => { _settings.Zoom.MagnificationLevel = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "z_grace", "Grace Period (ms)",
                    0, 2000, 50, 0,
                    () => _settings.Zoom.ZoomGracePeriodMs,
                    v => { _settings.Zoom.ZoomGracePeriodMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "z_reset", "Reset Zoom Settings",
                    () => ShowModal("Reset Zoom settings to defaults?", () =>
                    {
                        var z = new ZoomSettings();
                        _settings.Zoom.Enabled = z.Enabled;
                        _settings.Zoom.Position = z.Position;
                        _settings.Zoom.CircleRadiusPixels = z.CircleRadiusPixels;
                        _settings.Zoom.MagnificationLevel = z.MagnificationLevel;
                        _settings.Zoom.ZoomGracePeriodMs = z.ZoomGracePeriodMs;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }

    private List<List<Action>> BuildActionMenuPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellComboBox(_viewModel, "am_pos", "Position",
                    ["At Gaze", "At Center"],
                    () => _settings.ActionMenu.Position == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { _settings.ActionMenu.Position = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "am_reset", "Reset Action Menu Settings",
                    () => ShowModal("Reset Action Menu settings to defaults?", () =>
                    {
                        var a = new ActionMenuSettings();
                        _settings.ActionMenu.Position = a.Position;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }

    private List<List<Action>> BuildKeyboardPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellComboBox(_viewModel, "k_layout", "Active Layout",
                    ["QWERTY", "QWERTZ", "AZERTY"],
                    () => _settings.Keyboard.ActiveLayout,
                    v => { _settings.Keyboard.ActiveLayout = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "k_size", "Key Size Multiplier",
                    0.5, 2.0, 0.1, 1,
                    () => _settings.Keyboard.KeySizeMultiplier,
                    v => { _settings.Keyboard.KeySizeMultiplier = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellToggle(_viewModel, "k_pred", "Word Prediction",
                    () => _settings.Keyboard.PredictionEnabled,
                    v => { _settings.Keyboard.PredictionEnabled = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellComboBox(_viewModel, "k_lang", "Dictionary Language",
                    ["English", "French", "Spanish", "German", "Polish"],
                    ["en",      "fr",     "es",      "de",     "pl"],
                    () => string.IsNullOrEmpty(_settings.Keyboard.LanguagePack) ? "en"
                                   : _settings.Keyboard.LanguagePack,
                    v => { _settings.Keyboard.LanguagePack = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellToggle(_viewModel, "k_sound", "Click Sound",
                    () => _settings.Keyboard.ClickSoundEnabled,
                    v => { _settings.Keyboard.ClickSoundEnabled = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellComboBox(_viewModel, "k_edge", "Screen Edge",
                    ["Bottom", "Top"],
                    () => _settings.Keyboard.ScreenEdge,
                    v => { _settings.Keyboard.ScreenEdge = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "k_reset", "Reset Keyboard Settings",
                    () => ShowModal("Reset Keyboard settings to defaults?", () =>
                    {
                        var k = new KeyboardSettings();
                        _settings.Keyboard.ActiveLayout = k.ActiveLayout;
                        _settings.Keyboard.KeySizeMultiplier = k.KeySizeMultiplier;
                        _settings.Keyboard.PredictionEnabled = k.PredictionEnabled;
                        _settings.Keyboard.LanguagePack = "";
                        _settings.Keyboard.ScreenEdge = k.ScreenEdge;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }

    private List<List<Action>> BuildToolbarPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellToggle(_viewModel, "t_dock", "Dock to Screen Edge",
                    () => _settings.Toolbar.IsDocked,
                    v => { _settings.Toolbar.IsDocked = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellComboBox(_viewModel, "t_edge", "Docked Edge",
                    ["Top", "Bottom", "Left", "Right"],
                    () => _settings.Toolbar.DockedEdge,
                    v => { _settings.Toolbar.DockedEdge = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "t_size", "Button Size",
                    32, 120, 4, 0,
                    () => _settings.Toolbar.Size,
                    v => { _settings.Toolbar.Size = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "t_reset", "Reset Toolbar Settings",
                    () => ShowModal("Reset Toolbar settings to defaults?", () =>
                    {
                        var t = new ToolbarSettings();
                        _settings.Toolbar.IsDocked = t.IsDocked;
                        _settings.Toolbar.DockedEdge = t.DockedEdge;
                        _settings.Toolbar.Size = t.Size;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }

    private List<List<Action>> BuildThemePages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellComboBox(_viewModel, "th_mode", "Theme Mode",
                    ["System", "Light", "Dark"],
                    () => _settings.Theme.Mode,
                    v => { _settings.Theme.Mode = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "th_op", "Overlay Opacity",
                    0.3, 1.0, 0.1, 1,
                    () => _settings.Theme.OverlayOpacity,
                    v => { _settings.Theme.OverlayOpacity = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellToggle(_viewModel, "th_hc", "High Contrast",
                    () => _settings.Theme.HighContrast,
                    v => { _settings.Theme.HighContrast = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "th_reset", "Reset Theme Settings",
                    () => ShowModal("Reset Theme settings to defaults?", () =>
                    {
                        var th = new ThemeSettings();
                        _settings.Theme.Mode = th.Mode;
                        _settings.Theme.OverlayOpacity = th.OverlayOpacity;
                        _settings.Theme.HighContrast = th.HighContrast;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }

    private List<List<Action>> BuildGeneralPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellToggle(_viewModel, "g_boot", "Start on Windows Boot",
                    () => _settings.General.StartOnBoot,
                    v => { _settings.General.StartOnBoot = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellToggle(_viewModel, "g_pause", "Start Paused",
                    () => _settings.General.StartPaused,
                    v => { _settings.General.StartPaused = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "g_reset", "Reset General Settings",
                    () => ShowModal("Reset General settings to defaults?", () =>
                    {
                        var g = new GeneralSettings();
                        _settings.General.StartOnBoot = g.StartOnBoot;
                        _settings.General.StartPaused = g.StartPaused;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
                () => Add(new DwellButton(_viewModel, "g_resetall", "\u26A0 Reset ALL Settings",
                    () => ShowModal("Reset ALL settings to defaults?\nThis cannot be undone.", () =>
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
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }

    // ────── Shared helpers ──────

    private void Add(DwellControl control)
    {
        control.Attach(ContentPanel);
        _activeItems.Add(control);
    }

    private void Add(IDwellComposite composite)
    {
        composite.Attach(ContentPanel);
        _activeItems.Add(composite);
    }

    private void Add(DwellLabel label)
    {
        label.Attach(ContentPanel);
    }

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
            _viewModel.UpdatePosition("close", DwellCategory.Tab, new PhysicalPoint(cPos.X, cPos.Y));
        }
        catch (InvalidOperationException)
        {
            // Window or button not yet rendered or not in visual tree
        }

        // Update tab button positions
        UpdateButtonPositions(_tabButtons, (id, pos) => _viewModel.UpdatePosition(id, DwellCategory.Tab, pos));

        // Update pagination button positions
        UpdateButtonPositions(_pagButtons, (id, pos) => _viewModel.UpdatePosition(id, DwellCategory.Pagination, pos));

        // Update modal button positions if modal is visible
        if (_viewModel.IsModalVisible)
        {
            UpdateButtonPositions(_modalButtons, (id, pos) => _viewModel.UpdatePosition(id, DwellCategory.Modal, pos));
        }

        // Update new-style dwell controls
        foreach (var item in _activeItems)
        {
            if (item is DwellControl single)
            {
                single.UpdatePosition();
                single.UpdateVisual();
            }
            if (item is IDwellComposite comp)
            {
                foreach (var child in comp.Children)
                {
                    child.UpdatePosition();
                    child.UpdateVisual();
                }
            }
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
            _viewModel.Register("modal_yes", DwellCategory.Modal, DpiHelper.LogicalToPhysicalX(ModalBtnRadius), _dwellMs, () => { });
            _viewModel.Register("modal_no", DwellCategory.Modal, DpiHelper.LogicalToPhysicalX(ModalBtnRadius), _dwellMs, () => { });

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
                _viewModel.SimulateActivated(id, DwellCategory.Modal);
            else
                _viewModel.HideModal();
        };

        _modalButtons[id] = border;
        return border;
    }
}
