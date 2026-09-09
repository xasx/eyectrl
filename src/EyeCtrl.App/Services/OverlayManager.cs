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

    public CursorCircleWindow CursorCircle { get; private set; } = null!;
    public ActivationButtonWindow ActivationButton { get; private set; } = null!;
    public ManualSelectionOverlayWindow ManualSelectionOverlay { get; private set; } = null!;
    public ActionMenuWindow ActionMenu { get; private set; } = null!;
    public ToolbarWindow Toolbar { get; private set; } = null!;
    public PauseIndicatorWindow PauseIndicator { get; private set; } = null!;
    public KeyboardWindow Keyboard { get; private set; } = null!;
    public ManualSelectionGazeIndicatorWindow ManualSelectionIndicator { get; private set; } = null!;
    public GlassBackdropWindow GlassBackdrop { get; private set; } = null!;
    public CalibrationWindow Calibration { get; private set; } = null!;
    public TargetHighlightWindow TargetHighlight { get; private set; } = null!;
    /// <summary>
    /// The window itself. This class owns it, so it keeps the concrete type for the things only an
    /// owner does — binding a DataContext, calling <c>SyncButtons</c>, and driving
    /// <c>Window.Show</c>/<c>Window.Hide</c> from the view model's visibility. Consumers get
    /// <see cref="EdgeButtonBar"/> instead.
    /// </summary>
    private EdgeButtonBarWindow _edgeButtonBarWindow = null!;

    /// <inheritdoc />
    public IEdgeButtonBar EdgeButtonBar => _edgeButtonBarWindow;
    public TrackStatusWindow TrackStatus { get; private set; } = null!;
    public TaskSwitcherWindow TaskSwitcher { get; private set; } = null!;
    public QuickActionsGridWindow QuickActionsGrid { get; private set; } = null!;

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
        CursorCircle = new CursorCircleWindow();
        ActivationButton = new ActivationButtonWindow();
        ManualSelectionOverlay = new ManualSelectionOverlayWindow();
        ActionMenu = new ActionMenuWindow();
        Toolbar = new ToolbarWindow();
        Keyboard = new KeyboardWindow(_sink, _dwellRegistry);
        ManualSelectionIndicator = new ManualSelectionGazeIndicatorWindow();
        GlassBackdrop = new GlassBackdropWindow();
        TargetHighlight = new TargetHighlightWindow();
        _edgeButtonBarWindow = new EdgeButtonBarWindow();
        PauseIndicator = new PauseIndicatorWindow();

        // Configure ManualSelection indicator
        ManualSelectionIndicator.Configure(
            settings.ManualSelection.GazeIndicatorOuterDiameter,
            settings.ManualSelection.GazeIndicatorCenterDotDiameter,
            settings.ManualSelection.GazeIndicatorStrokeThickness,
            settings.ManualSelection.GazeIndicatorOpacity);

        // Set DataContext for windows that use MVVM binding
        CursorCircle.DataContext = viewModels.CursorCircle;
        ActivationButton.DataContext = viewModels.ActivationButton;
        ManualSelectionOverlay.DataContext = viewModels.ManualSelectionOverlay;
        ActionMenu.DataContext = viewModels.ActionMenu;
        Toolbar.DataContext = viewModels.Toolbar;
        Keyboard.DataContext = viewModels.Keyboard;
        Keyboard.SetViewModel(viewModels.Keyboard);

        Calibration = new CalibrationWindow();
        Calibration.SetViewModel(viewModels.Calibration);

        // Set DataContext for TargetHighlight
        TargetHighlight.DataContext = viewModels.TargetHighlight;

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
        CursorCircle.UpdateAppearance(settings.Cursor);
        Toolbar.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs);
        Toolbar.SetProviderIndicator(providerType);

        // Track status overlay (click-through, top-right). Capability-gated inside the VM.
        TrackStatus = new TrackStatusWindow();
        TrackStatus.PositionTopRight();
        TrackStatus.Configure(viewModels.TrackStatus);

        // Task Switcher overlay (fullscreen, not click-through). Icons resolved by the View.
        TaskSwitcher = new TaskSwitcherWindow();
        TaskSwitcher.Configure(viewModels.TaskSwitcher, _iconProvider);

        // Quick Actions grid overlay (fullscreen). Takes only the VM, no icon provider.
        QuickActionsGrid = new QuickActionsGridWindow();
        QuickActionsGrid.Configure(viewModels.QuickActionsGrid);

        _logger.LogInformation("Overlay windows initialized successfully");
    }
}
