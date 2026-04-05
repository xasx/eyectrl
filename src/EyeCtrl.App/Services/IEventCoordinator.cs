namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Windows;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// Coordinates event handlers between ViewModels, overlay windows, and the core engine.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public interface IEventCoordinator
{
    /// <summary>
    /// Initializes event handlers and message subscriptions.
    /// </summary>
    /// <param name="context">The event coordination context containing all dependencies</param>
    void Initialize(EventCoordinationContext context);

    /// <summary>
    /// Cleans up all event subscriptions and handlers.
    /// </summary>
    void Dispose();
}

/// <summary>
/// Context object containing all dependencies needed for event coordination.
/// </summary>
public record EventCoordinationContext(
    // ViewModels
    CursorCircleViewModel CursorVM,
    ActivationButtonViewModel ActivationVM,
    ZoomOverlayViewModel ZoomVM,
    ZoomGazeIndicatorViewModel ZoomIndicatorVM,
    ActionMenuViewModel ActionMenuVM,
    ToolbarViewModel ToolbarVM,
    KeyboardViewModel KeyboardVM,

    // Overlay Windows
    IOverlayManager OverlayManager,

    // Core
    GazeEngine GazeEngine,
    InteractionManager InteractionManager,

    // Services
    IMessenger Messenger,
    Dispatcher Dispatcher,
    SettingsService SettingsService,
    EyeCtrlSettings Settings,

    // Dependencies
    EyeCtrl.Platform.Input.IInputSimulator InputSimulator);
