namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Input;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.Logging;

/// <summary>
/// Manages the lifecycle of overlay windows in the application.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public class OverlayManager : IOverlayManager
{
    private readonly ILogger<OverlayManager> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly IAppIconProvider _iconProvider;
    private readonly IStruggleTelemetrySink? _sink;
    private readonly IDwellDetectorRegistry? _dwellRegistry;

    private TaskSwitcherWindow _taskSwitcher = null!;
    /// <inheritdoc />
    public ITaskSwitcherOverlay TaskSwitcher => _taskSwitcher;
    private QuickActionsGridWindow _quickActionsGrid = null!;
    /// <inheritdoc />
    public IQuickActionsGridOverlay QuickActionsGrid => _quickActionsGrid;
    private ModesMenuWindow _modesMenu = null!;
    /// <inheritdoc />
    public IModesMenuOverlay ModesMenu => _modesMenu;

    public OverlayManager(Dispatcher dispatcher, ILogger<OverlayManager> logger, IAppIconProvider iconProvider,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _iconProvider = iconProvider;
        _sink = sink;
        _dwellRegistry = dwellRegistry;
    }

    /// <summary>
    /// Initializes all overlay windows with their ViewModels and settings.
    /// </summary>
    public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings, GazeProviderType providerType)
    {
        _logger.LogDebug("Initializing overlay windows");

        // Create overlay windows
        _cursorCircle = new CursorCircleWindow();
        _activationButton = new ActivationButtonWindow();
        _manualSelectionOverlay = new ManualSelectionOverlayWindow();
        _actionMenu = new ActionMenuWindow();
        _toolbar = new ToolbarWindow();
        _keyboard = new KeyboardWindow(_sink, _dwellRegistry);
        _manualSelectionIndicator = new ManualSelectionGazeIndicatorWindow();
        _glassBackdrop = new GlassBackdropWindow();
        _targetHighlight = new TargetHighlightWindow();
        _edgeButtonBarWindow = new EdgeButtonBarWindow();
        _pauseIndicator = new PauseIndicatorWindow();

        // Configure ManualSelection indicator
        _manualSelectionIndicator.Configure(
            settings.ManualSelection.GazeIndicatorOuterDiameter,
            settings.ManualSelection.GazeIndicatorCenterDotDiameter,
            settings.ManualSelection.GazeIndicatorStrokeThickness,
            settings.ManualSelection.GazeIndicatorOpacity);

        // Set DataContext for windows that use MVVM binding
        _cursorCircle.DataContext = viewModels.CursorCircle;
        _activationButton.DataContext = viewModels.ActivationButton;
        _manualSelectionOverlay.DataContext = viewModels.ManualSelectionOverlay;
        _actionMenu.DataContext = viewModels.ActionMenu;
        _toolbar.DataContext = viewModels.Toolbar;
        _keyboard.DataContext = viewModels.Keyboard;
        _keyboard.SetViewModel(viewModels.Keyboard);

        _calibration = new CalibrationWindow();
        _calibration.SetViewModel(viewModels.Calibration);

        // Set DataContext for TargetHighlight
        _targetHighlight.DataContext = viewModels.TargetHighlight;

        _edgeButtonBarWindow.DataContext = viewModels.EdgeButtonBar;
        viewModels.EdgeButtonBar.ButtonSetChanged += () => _edgeButtonBarWindow.SyncButtons();
        viewModels.EdgeButtonBar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EdgeButtonBarViewModel.IsVisible))
            {
                if (viewModels.EdgeButtonBar.IsVisible)
                    _edgeButtonBarWindow.Show();
                else
                    // The cast is REQUIRED and must stay. EdgeButtonBarWindow declares
                    // `public new void Hide()`, which clears the view model — the very change that
                    // raised this notification. Calling it unqualified would hide nothing and
                    // re-enter the view model instead of the window. Casting to Window reaches
                    // Window.Hide, which is what "the VM went invisible" is supposed to do.
                    ((System.Windows.Window)_edgeButtonBarWindow).Hide();
            }
        };

        // Initialize overlays with their specific APIs
        _cursorCircle.UpdateAppearance(settings.Cursor);
        _toolbar.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs);
        _toolbar.SetProviderIndicator(providerType);

        // Track status overlay (click-through, top-right). Capability-gated inside the VM.
        _trackStatus = new TrackStatusWindow();
        _trackStatus.PositionTopRight();
        _trackStatus.Configure(viewModels.TrackStatus);

        // Task Switcher overlay (fullscreen, not click-through). Icons resolved by the View.
        _taskSwitcher = new TaskSwitcherWindow();
        _taskSwitcher.Configure(viewModels.TaskSwitcher, _iconProvider);

        // Quick Actions grid overlay (fullscreen). Takes only the VM, no icon provider.
        _quickActionsGrid = new QuickActionsGridWindow();
        _quickActionsGrid.Configure(viewModels.QuickActionsGrid);

        // Modes menu panel (small, under the toolbar). Takes the VM + toolbar height for placement.
        _modesMenu = new ModesMenuWindow();
        ModesMenu.Configure(viewModels.ModesMenu, settings.Toolbar.Size);

        _logger.LogInformation("Overlay windows initialized successfully");
    }
}
