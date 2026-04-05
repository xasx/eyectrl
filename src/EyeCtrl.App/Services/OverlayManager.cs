namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Input;
using Microsoft.Extensions.Logging;

/// <summary>
/// Manages the lifecycle of overlay windows in the application.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public class OverlayManager : IOverlayManager
{
    private readonly ILogger<OverlayManager> _logger;
    private readonly Dispatcher _dispatcher;

    public CursorCircleWindow CursorCircle { get; private set; } = null!;
    public ActivationButtonWindow ActivationButton { get; private set; } = null!;
    public ZoomOverlayWindow ZoomOverlay { get; private set; } = null!;
    public ZoomSideButtonWindow ZoomSideButtons { get; private set; } = null!;
    public ActionMenuWindow ActionMenu { get; private set; } = null!;
    public ToolbarWindow Toolbar { get; private set; } = null!;
    public KeyboardWindow Keyboard { get; private set; } = null!;
    public ZoomGazeIndicatorWindow ZoomIndicator { get; private set; } = null!;

    public OverlayManager(Dispatcher dispatcher, ILogger<OverlayManager> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>
    /// Initializes all overlay windows with their ViewModels and settings.
    /// </summary>
    public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings, string providerType)
    {
        _logger.LogDebug("Initializing overlay windows");

        // Create overlay windows
        CursorCircle = new CursorCircleWindow();
        ActivationButton = new ActivationButtonWindow();
        ZoomOverlay = new ZoomOverlayWindow();
        ZoomSideButtons = new ZoomSideButtonWindow();
        ActionMenu = new ActionMenuWindow();
        Toolbar = new ToolbarWindow();
        Keyboard = new KeyboardWindow();
        ZoomIndicator = new ZoomGazeIndicatorWindow();

        // Configure zoom indicator
        ZoomIndicator.Configure(
            settings.Zoom.GazeIndicatorOuterDiameter,
            settings.Zoom.GazeIndicatorCenterDotDiameter,
            settings.Zoom.GazeIndicatorStrokeThickness,
            settings.Zoom.GazeIndicatorOpacity);

        // Set DataContext for windows that use MVVM binding
        CursorCircle.DataContext = viewModels.CursorCircle;
        ActivationButton.DataContext = viewModels.ActivationButton;
        ZoomOverlay.DataContext = viewModels.ZoomOverlay;
        ActionMenu.DataContext = viewModels.ActionMenu;
        Toolbar.DataContext = viewModels.Toolbar;
        Keyboard.DataContext = viewModels.Keyboard;

        // Initialize overlays with their specific APIs
        CursorCircle.UpdateAppearance(settings.Cursor);
        Toolbar.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs);
        Toolbar.SetProviderIndicator(providerType);

        _logger.LogInformation("Overlay windows initialized successfully");
    }

    /// <summary>
    /// Initializes the keyboard overlay with input simulator and settings service.
    /// This is a separate method because it requires additional dependencies.
    /// </summary>
    public void InitializeKeyboard(
        EyeCtrlSettings settings,
        IInputSimulator inputSimulator,
        SettingsService settingsService)
    {
        if (Keyboard == null)
            throw new InvalidOperationException("Initialize must be called before InitializeKeyboard");

        // Load keyboard configuration (this method exists in App.xaml.cs)
        LoadKeyboardInternal(Keyboard, settings, inputSimulator, settingsService);
    }

    private void LoadKeyboardInternal(
        KeyboardWindow keyboard,
        EyeCtrlSettings settings,
        IInputSimulator inputSimulator,
        SettingsService settingsService)
    {
        // This is a placeholder - the actual implementation should be moved from App.xaml.cs
        // For now, we'll keep it in App.xaml.cs to avoid breaking changes
        // TODO: Move LoadKeyboard logic here in a future refactoring
        _logger.LogDebug("Keyboard initialization deferred to App.xaml.cs");
    }
}
