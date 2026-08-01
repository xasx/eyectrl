namespace EyeCtrl.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

public partial class SettingsWindow : OverlayWindow
{
    // ────── Constants ──────

    private const double ControlBtnRadius = 50;
    // Colour swatches are 90×75 px; 35 px keeps adjacent hit areas from overlapping.
    private const double ColorSwatchRadius = 35;
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

    private static readonly Color BtnBase = Tokens.SurfaceMedium;
    private static readonly Color TabActive = Tokens.SurfaceKeySwitch;
    private static readonly Color TabInactive = Tokens.SurfaceKey;
    private static readonly Color ResetBase = Tokens.AccentWarning;
    private static readonly Color CloseBase = Tokens.AccentDanger;
    private static readonly Color DeleteBase = Tokens.AccentWarning;

    // ────── State ──────

    private readonly SettingsWindowViewModel _viewModel;
    private readonly EyeCtrlSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly ModelRegistryService _registryService;
    private readonly ModelManagementService _modelManagementService;
    private readonly IMessenger _messenger;
    private readonly CalibrationService _calibrationService;
    private readonly AppRestartCoordinator _restartCoordinator;
    private readonly int _dwellMs;

    private readonly Dictionary<string, Border> _tabButtons = new();
    private readonly Dictionary<string, Border> _pagButtons = new();
    private readonly Dictionary<string, Border> _modalButtons = new();

    // One mutable brush per button, mutated in place on dwell ticks (mirrors KeyboardWindow._keyBrushes).
    // Never allocated per-tick: UpdateXxxVisuals only sets .Color, so dwell highlight has zero GC churn.
    private readonly Dictionary<string, SolidColorBrush> _tabBrushes = new();
    private readonly Dictionary<string, SolidColorBrush> _pagBrushes = new();
    private readonly Dictionary<string, SolidColorBrush> _modalBrushes = new();
    // Base fill per modal button id, captured at build time so UpdateModalVisuals can
    // blend each button's dwell progress from its own base rather than a single shared one.
    private readonly Dictionary<string, Color> _modalBaseColors = new();
    // Per-id dwell blend factor from ModalButtonPalette. The dark Neutral fill's light label
    // would collapse (to ~2.6:1 at full dwell) at the bright kinds' 0.5 factor; the palette
    // sizes the factor so the label stays legible through the whole dwell. See ModalButtonPalette.
    private readonly Dictionary<string, double> _modalBlendFactors = new();
    private SolidColorBrush? _closeButtonBrush;

    private List<List<Action>> _pages = new();
    private readonly List<object> _activeItems = new(); // DwellControl | IDwellComposite

    private enum QaView { List, PickType, PickShortcut, PickMedia, PickApp }
    private QaView _qaView = QaView.List;

    // Switch the Quick Actions sub-view and rebuild. RebuildContent() rebuilds _pages for the new
    // view and clamps CurrentPage into range, so we do NOT set CurrentPage here: setting it would
    // fire PropertyChanged → RenderCurrentPage() against the OLD view's _pages (a stale render)
    // before the rebuild runs. Every picker is entered from a page-0 context (Add-action lives only
    // on List page 0; the type picker is a single page), so pickers still open at the top; only the
    // return-to-List transition may land on an action row rather than the config page — which is
    // fine. Never reset CurrentPage inside BuildQuickActionsPages either — it fights pag_prev/next.
    private void GoToQaView(QaView view)
    {
        _qaView = view;
        RebuildContent();
    }

    public event Action<EyeCtrlSettings>? SettingsChanged;

    private readonly (string Id, string Icon, string Label, Func<List<List<Action>>> PageBuilder)[] _tabDefs;
    private readonly Dictionary<string, Func<List<List<Action>>>> _pageBuilders = new();

    public SettingsWindow(
        SettingsWindowViewModel viewModel,
        EyeCtrlSettings settings,
        SettingsService settingsService,
        ModelRegistryService registryService,
        ModelManagementService modelManagementService,
        IMessenger messenger,
        CalibrationService calibrationService,
        AppRestartCoordinator restartCoordinator)
    {
        InitializeComponent();

        // Replace the XAML-provided frozen background (shared resource BrushAccentDanger) with a
        // mutable brush so UpdateTabVisuals can mutate .Color in place on dwell ticks without
        // allocating. CloseBase == Tokens.AccentDanger, matching the XAML resource.
        _closeButtonBrush = new SolidColorBrush(CloseBase);
        CloseButton.Background = _closeButtonBrush;

        _viewModel = viewModel;
        _settings = settings;
        _settingsService = settingsService;
        _registryService = registryService;
        _modelManagementService = modelManagementService;
        _messenger = messenger;
        _calibrationService = calibrationService;
        _restartCoordinator = restartCoordinator;
        _dwellMs = settings.Dwell.ToolbarButtonDwellMs;

        // An accepted restart must also be visible. The handshake allows the replacement up
        // to 30s to announce itself; the dispatcher stays free, but silence for that long is
        // indistinguishable from a hang, and the in-flight guard drops a second dwell without
        // saying so. Replaced automatically by the refusal notice below if it fails, and
        // taken down with the process if it succeeds.
        //
        // The one button only HIDES the notice — it cannot cancel a restart whose child may
        // already be running, and it must exist: a modal with no dwell target would trap a
        // gaze-only user if the outcome never arrived.
        _messenger.Register<RestartInProgressMessage>(this, (_, _) =>
            Dispatcher.Invoke(() => _viewModel.ShowModal(
                "Restarting EyeCtrl…\n"
                + "Waiting for the new EyeCtrl to start. If it cannot, this one keeps running.\n"
                + "Hiding this notice does not stop the restart.",
                [new ModalChoice("restart_progress_hide", "✕ Hide", ModalChoiceKind.Neutral, null)])));

        // A refused restart must be visible: the user just completed a dwell, and
        // silence reads as the dwell not registering.
        //
        // ONE button, not the Yes/No pair. This is an acknowledgement, not a question —
        // "✕ No" to "the new EyeCtrl did not start in time" means nothing, and for a gaze
        // user every extra target is another chance to mis-dwell on a message that only
        // needs dismissing. The multi-choice modal makes a single button expressible;
        // before it existed this had to borrow the Yes/No form.
        _messenger.Register<RestartUnavailableMessage>(this, (_, m) =>
            Dispatcher.Invoke(() => _viewModel.ShowModal(
                m.Reason,
                [new ModalChoice("restart_failed_ok", "✓ OK", ModalChoiceKind.Affirmative, null)])));

        // Initialize tab definitions with page builders
        _tabDefs =
        [
            ("Cursor",   "◎", "Cursor",      BuildCursorPages),
            ("Dwell",    "⏱", "Dwell Times", BuildDwellPages),
            ("Selection", "⊕", "Selection", BuildSelectionPages),
            ("Actions",  "⊙", "Action Menu", BuildActionMenuPages),
            ("Layout",   "⊞", "Layout",      BuildLayoutPages),
            ("Keyboard", "⌨", "Keyboard",    BuildKeyboardPages),
            ("Models",    "⬡", "Models",      BuildModelsPages),
            ("Toolbar",  "▣", "Toolbar",     BuildToolbarPages),
            ("Calibration", "⌖", "Calibration", BuildCalibrationPages),
            ("General",  "⚙", "General",     BuildGeneralPages),
            ("QuickActions", "⚡", "Quick Actions", BuildQuickActionsPages)
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
                    _qaView = QaView.List;
                    RebuildContent();
                    break;
                case nameof(SettingsViewModel.CurrentPage):
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
                Foreground = Tokens.BrushTextPrimary,
                FontSize = 30,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(iconText, $"Settings_Tab_{id}");

            var tabBrush = new SolidColorBrush(TabInactive);
            var border = new Border
            {
                Height = 100,
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(8),
                Background = tabBrush,
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
                            Foreground = Tokens.BrushTextPrimary,
                            FontSize = 22,
                            FontWeight = FontWeights.SemiBold,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            };

            // Mouse fallback
            border.MouseLeftButtonDown += (_, _) => ShowTab(id);

            // Store border + brush for visual updates
            _tabButtons[id] = border;
            _tabBrushes[id] = tabBrush;
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
        _viewModel.Settings.TotalPages = _pages.Count;
        _viewModel.Settings.CurrentPage = Math.Clamp(_viewModel.Settings.CurrentPage, 0, Math.Max(0, _viewModel.Settings.TotalPages - 1));

        RenderCurrentPage();
        RenderPagination();
    }

    private void RenderCurrentPage()
    {
        _viewModel.ClearCategory(DwellCategory.Control);
        ContentPanel.Children.Clear();
        _activeItems.Clear();

        var currentPage = _viewModel.Settings.CurrentPage;
        if (currentPage < _pages.Count)
        {
            foreach (var builder in _pages[currentPage])
                builder();
        }
    }

    private void RenderPagination()
    {
        PaginationPanel.Children.Clear();
        _pagButtons.Clear();
        _pagBrushes.Clear();

        // Use ViewModel's pagination state
        if (_viewModel.Settings.TotalPages <= 1) return;

        var prev = MakePagButton("pag_prev", "◀");
        var indicator = new TextBlock
        {
            Text = $"{_viewModel.Settings.CurrentPage + 1} / {_viewModel.Settings.TotalPages}",
            Foreground = Tokens.BrushTextPrimary,
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
        var pagBrush = new SolidColorBrush(BtnBase);
        var btn = new Border
        {
            Width = 100,
            Height = 80,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            Background = pagBrush,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Tokens.BrushTextPrimary,
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
        _pagBrushes[id] = pagBrush;

        // Register with ViewModel for dwell detection
        _viewModel.Register(id, DwellCategory.Pagination, DpiHelper.LogicalToPhysicalX(PagBtnRadius), _dwellMs, () => { });

        return btn;
    }

    // ────── Page builders per tab ──────

    private List<List<Action>> BuildPages()
    {
        return _pageBuilders.TryGetValue(_viewModel.Settings.CurrentTabName, out var pageBuilder)
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
                    cat, DpiHelper.LogicalToPhysicalX(ColorSwatchRadius), _dwellMs)),
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
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80, textBrush: Tokens.BrushSurfaceDark)),
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
                () => Add(new DwellSlider(_viewModel, "d_ManualSelection", "ManualSelection Action Menu (ms)",
                    200, 1500, 50, 0,
                    () => _settings.Dwell.ManualSelectionActionMenuDwellMs,
                    v => { _settings.Dwell.ManualSelectionActionMenuDwellMs = (int)v; ApplyAndSave(); },
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
                        _settings.Dwell.ManualSelectionActionMenuDwellMs = d.ManualSelectionActionMenuDwellMs;
                        _settings.Dwell.ToolbarButtonDwellMs = d.ToolbarButtonDwellMs;
                        _settings.Dwell.KeyboardKeyDwellMs = d.KeyboardKeyDwellMs;
                        _settings.Dwell.KeyboardPredictionDwellMs = d.KeyboardPredictionDwellMs;
                        _settings.Dwell.FixationRadiusPixels = d.FixationRadiusPixels;
                        _settings.Dwell.DwellCooldownMs = d.DwellCooldownMs;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80, textBrush: Tokens.BrushSurfaceDark)),
            ]
        ];
    }

    private List<List<Action>> BuildSelectionPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellComboBox(_viewModel, "s_mode", "Selection Mode",
                    ["None", "Manual Selection", "Auto Selection"],
                    ["None", "ManualSelection", "AutoSelection"],
                    () => _settings.SelectionMode.ToString(),
                    v =>
                    {
                        _settings.SelectionMode = Enum.Parse<EyeCtrl.Core.Models.SelectionMode>(v);
                        ApplyAndSave();
                    },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "s_rad", "Circle Radius (px)",
                    50, 300, 10, 0,
                    () => _settings.ManualSelection.CircleRadiusPixels,
                    v => { _settings.ManualSelection.CircleRadiusPixels = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "s_mag", "Magnification Level",
                    1.5, 5, 0.5, 1,
                    () => _settings.ManualSelection.MagnificationLevel,
                    v => { _settings.ManualSelection.MagnificationLevel = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "s_grace", "Grace Period (ms)",
                    0, 2000, 50, 0,
                    () => _settings.ManualSelection.ManualSelectionGracePeriodMs,
                    v => { _settings.ManualSelection.ManualSelectionGracePeriodMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellColorPicker(_viewModel, "s_highlight", "Highlight Color",
                    () => _settings.AutoSelection.HighlightColor,
                    v => { _settings.AutoSelection.HighlightColor = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "s_highlightThickness", "Highlight Thickness",
                    1, 10, 1, 0,
                    () => _settings.AutoSelection.HighlightThickness,
                    v => { _settings.AutoSelection.HighlightThickness = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
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
                () => Add(new DwellButton(_viewModel, "am_reset", "Reset Action Menu Settings",
                    () => ShowModal("Reset Action Menu settings to defaults?", () =>
                    {
                        var a = new ActionMenuSettings();
                        _settings.ActionMenu.VisibleActions = new List<InteractionAction>(a.VisibleActions);
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80, textBrush: Tokens.BrushSurfaceDark)),
            ]
        ];
    }

    private List<List<Action>> BuildLayoutPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellComboBox(_viewModel, "lay_pos", "Position",
                    ["At Gaze", "At Center"],
                    () => _settings.ViewPosition == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { _settings.ViewPosition = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "lay_reset", "Reset Layout Settings",
                    () => ShowModal("Reset Layout settings to defaults?", () =>
                    {
                        var d = new EyeCtrlSettings();
                        _settings.ViewPosition = d.ViewPosition;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80, textBrush: Tokens.BrushSurfaceDark)),
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
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80, textBrush: Tokens.BrushSurfaceDark)),
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
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80, textBrush: Tokens.BrushSurfaceDark)),
            ]
        ];
    }

    private List<List<Action>> BuildModelsPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        var page1 = new List<Action>();

        var downloadedModels = _registryService.SelectableModels;
        if (downloadedModels.Count == 0)
        {
            page1.Add(() => Add(new DwellLabel("No models downloaded. Download one below.")));
        }
        else
        {
            var displayNames = downloadedModels.Select(m => m.DisplayName).ToList();
            var modelIds = downloadedModels.Select(m => m.Id).ToList();
            page1.Add(() => Add(new DwellComboBox(_viewModel, "md_active", "Active Model",
                displayNames.Prepend("Off").ToArray(),
                modelIds.Prepend("Off").ToArray(),
                () => _registryService.ActiveModelId ?? "Off",
                v =>
                {
                    _modelManagementService.SetActiveModel(v);
                    ApplyAndSave();
                },
                cat, r, _dwellMs)));
        }

        page1.Add(() => Add(new DwellLabel(new string('\u2500', 50))));

        var allEntries = _registryService.Registry.Values.OrderBy(e => e.SizeBytes).ToList();
        foreach (var entry in allEntries)
        {
            var entryId = entry.Id;
            var displayName = entry.DisplayName;
            var sizeText = FormatSize(entry.SizeBytes);

            if (_registryService.IsModelDownloaded(entryId))
            {
                page1.Add(() => Add(new DwellLabel($"  {displayName}  {sizeText}  \u2713 Downloaded")));
            }
            else
            {
                page1.Add(() =>
                {
                    var label = $"{displayName}  {sizeText}  [Download]";
                    Add(new DwellButton(_viewModel, $"md_dl_{entryId}", label,
                        () =>
                        {
                            if (_modelManagementService.IsDownloading)
                                return;
                            ShowModal($"Download {displayName} ({sizeText})?", () =>
                                _ = DownloadBundledModelAsync(entryId, entry.Url, entry.Sha256,
                                    entry.TokenizerUrl, entry.TokenizerSha256));
                        },
                        BtnBase, cat, r, _dwellMs, width: 400, height: 60));
                });
            }
        }

        page1.Add(() => Add(new DwellLabel(new string('\u2500', 50))));

        page1.Add(() => Add(new DwellButton(_viewModel, "md_add", "\uff0b Add Custom Model (paste URL)",
            () => DownloadCustomModelFromClipboard(),
            BtnBase, cat, r, _dwellMs, width: 300, height: 60)));

        var manageableModels = _registryService.DownloadedModels;
        if (manageableModels.Count > 0)
        {
            page1.Add(() => Add(new DwellButton(_viewModel, "md_manage", "Manage models...",
                () => _viewModel.Settings.NextPage(),
                BtnBase, cat, r, _dwellMs, width: 300, height: 60)));
        }

        var page2 = new List<Action>();
        if (manageableModels.Count > 0)
        {
            foreach (var entry in manageableModels)
            {
                var entryId = entry.Id;
                var displayName = entry.DisplayName;
                var sizeText = FormatSize(entry.SizeBytes);
                page2.Add(() => Add(new DwellButton(_viewModel, $"md_del_{entryId}", $"{displayName}  {sizeText}  \uD83D\uDDD1 Delete",
                    () => ShowModal($"Delete {displayName}? This cannot be undone.", () =>
                    {
                        _modelManagementService.DeleteModel(entryId);
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    DeleteBase, cat, r, _dwellMs, width: 400, height: 60, textBrush: Tokens.BrushSurfaceDark)));
            }

            page2.Add(() => Add(new DwellButton(_viewModel, "md_back", "Back to models",
                () => _viewModel.Settings.PreviousPage(),
                BtnBase, cat, r, _dwellMs, width: 300, height: 60)));
        }

        return page2.Count > 0 ? [page1, page2] : [page1];
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576:F0} MB";
        if (bytes >= 1024) return $"{bytes / 1024:F0} KB";
        return $"{bytes} B";
    }

    private async Task DownloadBundledModelAsync(string modelId, string url, string sha256,
        string tokenizerUrl, string tokenizerSha256)
    {
        try
        {
            var result = await _modelManagementService.DownloadBundledModelAsync(
                modelId, url, sha256, tokenizerUrl, tokenizerSha256);
            Dispatcher.Invoke(() =>
            {
                if (!result.IsSuccess)
                    ShowError(result.Error ?? "Download failed.");
                RebuildContent();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                ShowError($"Download failed: {ex.Message}");
                RebuildContent();
            });
        }
    }

    private void DownloadCustomModelFromClipboard()
    {
        var url = System.Windows.Clipboard.GetText()?.Trim() ?? "";
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://"))
        {
            ShowError("Copy a valid HTTPS model URL to clipboard first, then dwell this button.");
            return;
        }

        if (_modelManagementService.IsDownloading)
        {
            ShowError("Another download is already in progress.");
            return;
        }

        var displayName = ModelManagementService.DeriveModelName(url);
        ShowModal($"Download custom model \"{displayName}\" from clipboard URL?", () =>
        {
            _ = Task.Run(async () =>
            {
                var result = await _modelManagementService.DownloadCustomModelAsync(url);
                Dispatcher.Invoke(() =>
                {
                    if (!result.IsSuccess)
                        ShowError(result.Error ?? "Download failed.");
                    else
                        ApplyAndSave();
                    RebuildContent();
                });
            });
        });
    }

    private void ShowError(string message)
    {
        ShowModal(message, () => { });
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
                () => Add(new DwellToggle(_viewModel, "g_track", "Show Track Status",
                    () => _settings.TrackStatus.Enabled,
                    v => { _settings.TrackStatus.Enabled = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "g_restart", "↻ Restart Now",
                    () =>
                    {
                        // Only offer what is installed and not already running.
                        if (_restartCoordinator.ShouldOfferSupervisor())
                        {
                            _viewModel.ShowModal(
                                "Restart EyeCtrl now?\n"
                                + "A supervisor is installed. It restarts EyeCtrl if it ever "
                                + "fails, and can recover a restart that goes wrong.\n"
                                + "Starting it turns supervision on and leaves it on.",
                                [
                                    new ModalChoice("restart_supervised", "↻ Restart with supervisor",
                                        ModalChoiceKind.Affirmative,
                                        () => _messenger.Send(new RestartAppRequestedMessage(StartSupervisor: true))),
                                    new ModalChoice("restart_plain", "↻ Restart without",
                                        ModalChoiceKind.Neutral,
                                        () => _messenger.Send(new RestartAppRequestedMessage())),
                                    new ModalChoice("restart_cancel", "✕ Cancel",
                                        ModalChoiceKind.Negative, null),
                                ]);
                        }
                        else
                        {
                            ShowModal(
                                "Restart EyeCtrl now?",
                                () => _messenger.Send(new RestartAppRequestedMessage()));
                        }
                    },
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80,
                    textBrush: Tokens.BrushSurfaceDark)),
                () => Add(new DwellButton(_viewModel, "g_reset", "Reset General Settings",
                    () => ShowModal("Reset General settings to defaults?", () =>
                    {
                        var g = new GeneralSettings();
                        _settings.General.StartOnBoot = g.StartOnBoot;
                        _settings.General.StartPaused = g.StartPaused;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80, textBrush: Tokens.BrushSurfaceDark)),
                () => Add(new DwellButton(_viewModel, "g_resetall", "\u26A0 Reset ALL Settings",
                    () => ShowModal("Reset ALL settings to defaults?\nThis cannot be undone.", () =>
                    {
                        _viewModel.Settings.ResetAllSettings();
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80, textBrush: Tokens.BrushSurfaceDark)),
            ]
        ];
    }

    private const int QaActionsPerPage = 4;      // action rows per List page (pages 2+)
    private const int QaPresetsPerPage = 6;      // preset/app buttons per picker page

    private static string QaTypeGlyph(QuickActionType t) => t switch
    {
        QuickActionType.KeyboardShortcut => "⌨",       // ⌨
        QuickActionType.CannedText => "T",
        QuickActionType.LaunchExe => "⊞",              // ⊞
        QuickActionType.LaunchWindowsApp => "▦",       // BMP launch-app glyph (grid tiles use 🪟 separately)
        QuickActionType.LaunchUrl => "@",
        QuickActionType.MediaControl => "♫",           // ♫
        _ => "⚡",                                      // ⚡
    };

    private List<List<Action>> BuildQuickActionsPages() => _qaView switch
    {
        QaView.PickType => BuildQaTypePickerPages(),
        QaView.PickShortcut => BuildQaShortcutPages(),
        QaView.PickMedia => BuildQaMediaPages(),
        QaView.PickApp => BuildQaAppPages(),
        _ => BuildQaListPages(),
    };

    private List<List<Action>> BuildQaListPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;
        var qa = _settings.QuickActions;
        var editor = _viewModel.QuickActionsEditor;

        var pages = new List<List<Action>>();

        // Page 1 (pinned): config controls + "Add action".
        pages.Add(new List<Action>
        {
            () => Add(new DwellToggle(_viewModel, "qa_enabled", "Enable Quick Actions",
                () => qa.Enabled, v => { qa.Enabled = v; ApplyAndSave(); }, cat, r, _dwellMs)),
            () => Add(new DwellComboBox(_viewModel, "qa_edge", "Summon Edge",
                new[] { "Left", "Right", "Top", "Bottom" },
                () => qa.SummonEdge, v => { qa.SummonEdge = v; ApplyAndSave(); }, cat, r, _dwellMs)),
            () => Add(new DwellSlider(_viewModel, "qa_summon", "Summon Threshold (ms)",
                200, 2000, 50, 0,
                () => qa.SummonThresholdMs, v => { qa.SummonThresholdMs = (int)v; ApplyAndSave(); }, cat, r, _dwellMs)),
            () => Add(new DwellSlider(_viewModel, "qa_dismiss", "Dismiss Timeout (ms)",
                1000, 15000, 500, 0,
                () => qa.DismissTimeoutMs, v => { qa.DismissTimeoutMs = (int)v; ApplyAndSave(); }, cat, r, _dwellMs)),
            () => Add(new DwellSlider(_viewModel, "qa_columns", "Grid Columns",
                2, 5, 1, 0,
                () => qa.GridColumns, v => { qa.GridColumns = (int)v; ApplyAndSave(); }, cat, r, _dwellMs)),
            () => Add(new DwellButton(_viewModel, "qa_add", "＋  Add action",
                () => GoToQaView(QaView.PickType), BtnBase, cat, r, _dwellMs, width: 360, height: 80)),
        });

        // Pages 2+: one row per action (enable toggle + delete), chunked.
        var actions = editor.Actions.ToList();
        for (int i = 0; i < actions.Count; i += QaActionsPerPage)
        {
            var chunk = actions.Skip(i).Take(QaActionsPerPage).ToList();
            var page = new List<Action>();
            foreach (var action in chunk)
            {
                var a = action; // capture per iteration
                var caption = $"{QaTypeGlyph(a.Type)}  {a.Label}";
                page.Add(() => Add(new DwellToggle(_viewModel, "qa_en_" + a.Id, caption,
                    () => a.Enabled, _ => { editor.ToggleEnabled(a.Id); }, cat, r, _dwellMs)));
                page.Add(() => Add(new DwellButton(_viewModel, "qa_del_" + a.Id, "✕  Delete",
                    () => ShowModal($"Delete \"{a.Label}\"?", () => { editor.DeleteAction(a.Id); RebuildContent(); }),
                    DeleteBase, cat, r, _dwellMs, width: 360, height: 70, textBrush: Tokens.BrushSurfaceDark)));
            }
            pages.Add(page);
        }

        return pages;
    }

    private List<Action> QaBackButton(int page) => new()
    {
        () => Add(new DwellButton(_viewModel, "qa_back_" + page, "←  Back",
            () => GoToQaView(QaView.List), BtnBase,
            DwellCategory.Control, DpiHelper.LogicalToPhysicalX(ControlBtnRadius), _dwellMs,
            width: 360, height: 70)),
    };

    private List<List<Action>> BuildQaTypePickerPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        var page = QaBackButton(0);
        page.Add(() => Add(new DwellButton(_viewModel, "qa_type_kbd", "⌨  Keyboard Shortcut",
            () => GoToQaView(QaView.PickShortcut), BtnBase, cat, r, _dwellMs, width: 420, height: 90)));
        page.Add(() => Add(new DwellButton(_viewModel, "qa_type_media", "♫  Media Control",
            () => GoToQaView(QaView.PickMedia), BtnBase, cat, r, _dwellMs, width: 420, height: 90)));
        page.Add(() => Add(new DwellButton(_viewModel, "qa_type_app", "▦  Launch App",
            () => GoToQaView(QaView.PickApp), BtnBase, cat, r, _dwellMs, width: 420, height: 90)));
        return new List<List<Action>> { page };
    }

    private List<List<Action>> BuildQaShortcutPages()
    {
        var editor = _viewModel.QuickActionsEditor;

        return ChunkPickerPages(QuickActionPresets.KeyboardShortcuts.Count, "qa_ks", i =>
        {
            var (label, chord) = QuickActionPresets.KeyboardShortcuts[i];
            return (label, () => { editor.AddKeyboardShortcut(label, chord); GoToQaView(QaView.List); });
        });
    }

    private List<List<Action>> BuildQaMediaPages()
    {
        var editor = _viewModel.QuickActionsEditor;

        return ChunkPickerPages(QuickActionPresets.MediaControls.Count, "qa_md", i =>
        {
            var (label, command) = QuickActionPresets.MediaControls[i];
            return (label, () => { editor.AddMediaControl(label, command); GoToQaView(QaView.List); });
        });
    }

    private List<List<Action>> BuildQaAppPages()
    {
        var editor = _viewModel.QuickActionsEditor;
        var apps = editor.InstalledApps;
        if (apps.Count == 0)
        {
            var empty = QaBackButton(0);
            empty.Insert(0, () => Add(new DwellLabel("No installed apps found.")));
            return new List<List<Action>> { empty };
        }
        return ChunkPickerPages(apps.Count, "qa_app", i =>
        {
            var app = apps[i];
            return (app.DisplayName, () => { editor.AddApp(app); GoToQaView(QaView.List); });
        });
    }

    // Builds paginated picker pages of big dwell buttons; every page starts with a Back button.
    private List<List<Action>> ChunkPickerPages(int count, string idPrefix, Func<int, (string Label, Action OnPick)> itemAt)
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;
        var pages = new List<List<Action>>();

        for (int start = 0, page = 0; start < count; start += QaPresetsPerPage, page++)
        {
            var actions = QaBackButton(page);
            for (int i = start; i < Math.Min(start + QaPresetsPerPage, count); i++)
            {
                var (label, onPick) = itemAt(i);
                var id = $"{idPrefix}_{i}";
                actions.Add(() => Add(new DwellButton(_viewModel, id, label, onPick,
                    BtnBase, cat, r, _dwellMs, width: 460, height: 80)));
            }
            pages.Add(actions);
        }
        return pages;
    }

    private List<List<Action>> BuildCalibrationPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;
        var cal = _settings.Calibration;

        return
        [
            [
                () => Add(new DwellSlider(_viewModel, "cal_points", "Calibration Points",
                    3, 16, 1, 0,
                    () => cal.PointCount,
                    v => { cal.PointCount = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "cal_samples", "Samples per Point",
                    5, 100, 5, 0,
                    () => cal.SamplesPerPoint,
                    v => { cal.SamplesPerPoint = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "cal_hold", "Target Hold (ms)",
                    200, 3000, 100, 0,
                    () => cal.TargetHoldMs,
                    v => { cal.TargetHoldMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "cal_settle", "Settle Window (ms)",
                    0, 500, 25, 0,
                    () => cal.SettleWindowMs,
                    v => { cal.SettleWindowMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
            ],
            [
                () => Add(new DwellLabel(CalibrationStatusText())),
                () => Add(new DwellButton(_viewModel, "cal_run", "Calibrate Now",
                    () => { Close(); _messenger.Send(new CalibrationRequestedMessage()); },
                    BtnBase, cat, r, _dwellMs, width: 300, height: 80)),
                () => Add(new DwellButton(_viewModel, "cal_clear", "Clear Calibration",
                    () => ShowModal("Clear saved calibration?", () =>
                    {
                        _messenger.Send(new CalibrationClearRequestedMessage());
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80, textBrush: Tokens.BrushSurfaceDark)),
            ]
        ];
    }

    private string CalibrationStatusText()
    {
        var data = _calibrationService.CurrentData;
        if (data == null) return "Last calibration: none";
        return $"Last calibration: {data.CalibratedAt.LocalDateTime:g}  ·  "
            + $"{data.ComputeMeanErrorPixels():F1} px ({data.ComputeQualityPercent():F1}%)";
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
                // HighlightColor(CloseBase, progress, 0.5) == Blend(CloseBase, Accent, progress * 0.5)
                if (_closeButtonBrush != null)
                    _closeButtonBrush.Color = ColorBlend.Blend(CloseBase, Tokens.Accent, progress * 0.5);
                continue;
            }

            if (_tabBrushes.TryGetValue(id, out var brush) && id != _viewModel.ActiveTabId)
            {
                brush.Color = ColorBlend.Blend(TabInactive, TabActive, progress);
            }
        }
    }

    private void UpdatePaginationVisuals()
    {
        foreach (var (id, progress) in _viewModel.PagProgress)
        {
            if (_pagBrushes.TryGetValue(id, out var brush))
            {
                // HighlightColor(BtnBase, progress, 0.4) == Blend(BtnBase, Accent, progress * 0.4)
                brush.Color = ColorBlend.Blend(BtnBase, Tokens.Accent, progress * 0.4);
            }
        }
    }

    private void UpdateModalVisuals()
    {
        foreach (var (id, progress) in _viewModel.ModalProgress)
        {
            if (_modalBrushes.TryGetValue(id, out var brush)
                && _modalBaseColors.TryGetValue(id, out var baseC)
                && _modalBlendFactors.TryGetValue(id, out var factor))
            {
                // Blend toward Accent by progress * factor. The factor is per-kind from
                // ModalButtonPalette: 0.5 for the bright fills (their dark labels only gain
                // contrast), 0.25 for the dark Neutral fill (whose light label would collapse
                // to ~2.6:1 at full dwell under 0.5). Same Accent target preserves the app-wide
                // "accent means dwell in progress" cue; only the intensity adapts to the fill.
                brush.Color = ColorBlend.Blend(baseC, Tokens.Accent, progress * factor);
            }
        }
    }

    private void UpdateActiveTabHighlight()
    {
        foreach (var (id, brush) in _tabBrushes)
        {
            brush.Color = id == _viewModel.ActiveTabId ? TabActive : TabInactive;
        }
    }

    private void UpdateModalVisibility()
    {
        if (_viewModel.IsModalVisible)
        {
            ModalMessage.Text = _viewModel.ModalMessage;
            ModalButtonPanel.Children.Clear();
            _modalButtons.Clear();
            _modalBrushes.Clear();
            _modalBaseColors.Clear();
            _modalBlendFactors.Clear();

            // Two choices keep the horizontal row they have always had. Three or more
            // stack vertically: side by side they would not fit descriptive labels, and a
            // vertical stack gives a dwell user more separation between targets.
            var choices = _viewModel.ModalChoices;
            var vertical = choices.Count > 2;
            ModalButtonPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;

            foreach (var choice in choices)
            {
                var (background, foreground) = ModalButtonPalette.For(choice.Kind);
                var button = MakeModalButton(choice.Id, choice.Label, background, foreground, vertical);
                // Capture this kind's dwell blend factor alongside the base color MakeModalButton
                // stored, so UpdateModalVisuals blends at the per-kind intensity.
                _modalBlendFactors[choice.Id] = ModalButtonPalette.DwellBlendFactor(choice.Kind);
                ModalButtonPanel.Children.Add(button);

                // Register as a dwell target with DPI-scaled radius. The empty callback is
                // the extension point; the ViewModel runs the choice's own action.
                _viewModel.Register(
                    choice.Id, DwellCategory.Modal,
                    DpiHelper.LogicalToPhysicalX(ModalBtnRadius), _dwellMs, () => { });
            }

            ModalOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            _modalButtons.Clear();
            _modalBrushes.Clear();
            _modalBaseColors.Clear();
            _modalBlendFactors.Clear();
        }
    }

    // ────── Modal confirmation ──────

    private void ShowModal(string message, Action onYes)
    {
        // Delegate to ViewModel
        _viewModel.ShowModal(message, onYes);
    }

    /// <summary>Builds one modal button.</summary>
    /// <param name="id">Dwell-target id.</param>
    /// <param name="text">Label.</param>
    /// <param name="bg">Fill, from <see cref="ModalButtonPalette"/>.</param>
    /// <param name="foreground">Label brush, paired with the fill for contrast.</param>
    /// <param name="wide">
    /// True for a vertically stacked layout, where buttons are wider (descriptive labels)
    /// and separated by vertical margins instead of horizontal ones.
    /// </param>
    private Border MakeModalButton(string id, string text, Color bg, SolidColorBrush foreground, bool wide)
    {
        var modalBrush = new SolidColorBrush(bg);
        var border = new Border
        {
            Width = wide ? 460 : 170,
            Height = 80,
            Margin = wide ? new Thickness(0, 8, 0, 8) : new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(8),
            Background = modalBrush,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            }
        };

        // Mouse fallback. Everything routes through the ViewModel now — it owns which
        // choice does what, so the view no longer branches on the id.
        border.MouseLeftButtonDown += (_, _) => _viewModel.SimulateActivated(id, DwellCategory.Modal);

        _modalButtons[id] = border;
        _modalBrushes[id] = modalBrush;
        _modalBaseColors[id] = bg;
        return border;
    }
}
