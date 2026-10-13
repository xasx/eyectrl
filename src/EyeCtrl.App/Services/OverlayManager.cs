namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.Core.ActionMenu;
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

    private CursorCircleWindow _cursorCircle = null!;
    /// <inheritdoc />
    public ICursorCircleOverlay CursorCircle => _cursorCircle;
    private ActivationButtonWindow _activationButton = null!;
    /// <inheritdoc />
    public IActivationButtonOverlay ActivationButton => _activationButton;
    private ManualSelectionOverlayWindow _manualSelectionOverlay = null!;
    /// <inheritdoc />
    public IManualSelectionOverlay ManualSelectionOverlay => _manualSelectionOverlay;
    private ActionMenuWindow _actionMenu = null!;
    /// <inheritdoc />
    public IActionMenuOverlay ActionMenu => _actionMenu;
    private ToolbarWindow _toolbar = null!;
    /// <inheritdoc />
    public IToolbarOverlay Toolbar => _toolbar;
    private PauseIndicatorWindow _pauseIndicator = null!;
    /// <inheritdoc />
    public IPauseIndicatorOverlay PauseIndicator => _pauseIndicator;
    private KeyboardWindow _keyboard = null!;
    /// <inheritdoc />
    public KeyboardWindow Keyboard => _keyboard;
    private ManualSelectionGazeIndicatorWindow _manualSelectionIndicator = null!;
    /// <inheritdoc />
    public IManualSelectionIndicatorOverlay ManualSelectionIndicator => _manualSelectionIndicator;
    private GlassBackdropWindow _glassBackdrop = null!;
    /// <inheritdoc />
    public IGlassBackdropOverlay GlassBackdrop => _glassBackdrop;
    private CalibrationWindow _calibration = null!;
    /// <inheritdoc />
    public ICalibrationOverlay Calibration => _calibration;
    private TargetHighlightWindow _targetHighlight = null!;
    /// <inheritdoc />
    public ITargetHighlightOverlay TargetHighlight => _targetHighlight;
    /// <summary>
    /// The window itself. This class owns it, so it keeps the concrete type for the things only an
    /// owner does — binding a DataContext, calling <c>SyncButtons</c>, and driving
    /// <c>Window.Show</c>/<c>Window.Hide</c> from the view model's visibility. Consumers get
    /// <see cref="EdgeButtonBar"/> instead.
    /// </summary>
    private EdgeButtonBarWindow _edgeButtonBarWindow = null!;

    /// <inheritdoc />
    public IEdgeButtonBar EdgeButtonBar => _edgeButtonBarWindow;

    private EdgeButtonBarWindow _actionMenuModBarWindow = null!;

    /// <inheritdoc />
    public IEdgeButtonBar ActionMenuModBar => _actionMenuModBarWindow;
    private TrackStatusWindow _trackStatus = null!;
    /// <inheritdoc />
    public ITrackStatusOverlay TrackStatus => _trackStatus;
    private TaskSwitcherWindow _taskSwitcher = null!;
    /// <inheritdoc />
    public ITaskSwitcherOverlay TaskSwitcher => _taskSwitcher;
    private QuickActionsGridWindow _quickActionsGrid = null!;
    /// <inheritdoc />
    public IQuickActionsGridOverlay QuickActionsGrid => _quickActionsGrid;
    private ModesMenuWindow _modesMenu = null!;
    /// <inheritdoc />
    public IModesMenuOverlay ModesMenu => _modesMenu;
    private RepeatCursorWindow _repeatCursor = null!;
    /// <inheritdoc />
    public IRepeatCursorOverlay RepeatCursor => _repeatCursor;

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
        _actionMenuModBarWindow = new EdgeButtonBarWindow();
        _pauseIndicator = new PauseIndicatorWindow();

        // Configure ManualSelection indicator
        _manualSelectionIndicator.Configure(
            settings.ManualSelection.GazeIndicatorOuterDiameter,
            settings.ManualSelection.GazeIndicatorCenterDotDiameter,
            settings.ManualSelection.GazeIndicatorStrokeThickness,
            settings.ManualSelection.GazeIndicatorOpacity,
            settings.ManualSelection.HoldRadiusLogical);

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

        // The LEFT-edge modifier bar, wired exactly like the right-edge bar above. Owned
        // exclusively by the action menu (OverlayMessageCoordinator's show/hide handlers), so
        // nothing here differs except which view model drives it.
        _actionMenuModBarWindow.DataContext = viewModels.ActionMenuModBar;
        viewModels.ActionMenuModBar.ButtonSetChanged += () => _actionMenuModBarWindow.SyncButtons();
        viewModels.ActionMenuModBar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EdgeButtonBarViewModel.IsVisible))
            {
                if (viewModels.ActionMenuModBar.IsVisible)
                    _actionMenuModBarWindow.Show();
                else
                    // Same REQUIRED cast as above: EdgeButtonBarWindow's `public new void Hide()`
                    // clears the view model, which is the change that raised this notification.
                    // Window.Hide is what "the VM went invisible" is supposed to do.
                    ((System.Windows.Window)_actionMenuModBarWindow).Hide();
            }
        };

        // Initialize overlays with their specific APIs
        _cursorCircle.UpdateAppearance(settings.Cursor);
        // includeReset: Adaptation.Enabled — Reset is offered only while adaptation can have
        // learned anything to reset (Task 7, D5/M5). Live Enabled flips re-build the band through
        // SettingsUiCoordinator's AdaptationSettings.PropertyChanged subscription (Task 9);
        // at startup the setting is read once, here.
        _toolbar.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs,
            settings.Adaptation.Enabled);
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
        // T08 (N2): its icon-only buttons publish the overlays-keyed action names (D11); the live
        // ring's own source is the same seam.
        _modesMenu = new ModesMenuWindow();
        _modesMenu.Labels = RingLabelSource.Overlays;
        _modesMenu.Configure(viewModels.ModesMenu, settings.Toolbar.Size);

        // Contracting locked-mode aiming cursor (bound by the VM coordinator); sized from the
        // user's cursor-circle preference (field round 2: "slightly bigger").
        _repeatCursor = new RepeatCursorWindow();
        _repeatCursor.Configure(settings.Cursor.CircleSize);
        _repeatCursor.DataContext = viewModels.RepeatCursor;

        _logger.LogInformation("Overlay windows initialized successfully");
    }
}
