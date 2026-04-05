namespace EyeCtrl.App.Tests.Services;

using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Windows;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Input;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

public class EventCoordinatorTests : IDisposable
{
    private readonly Mock<IOverlayManager> _mockOverlayManager;
    private readonly Mock<IGazeProvider> _mockGazeProvider;
    private readonly Mock<IMouseCoordinateMapper> _mockMouseMapper;
    private readonly Mock<IInputSimulator> _mockInputSimulator;
    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;
    private readonly GazeEngine _gazeEngine;
    private readonly Dispatcher _dispatcher;
    private readonly SettingsService _settingsService;
    private readonly EventCoordinator _coordinator;

    // ViewModels
    private readonly CursorCircleViewModel _cursorVM;
    private readonly ActivationButtonViewModel _activationVM;
    private readonly ZoomOverlayViewModel _zoomVM;
    private readonly ZoomGazeIndicatorViewModel _zoomIndicatorVM;
    private readonly ActionMenuViewModel _actionMenuVM;
    private readonly ToolbarViewModel _toolbarVM;
    private readonly KeyboardViewModel _keyboardVM;

    public EventCoordinatorTests()
    {
        _mockOverlayManager = new Mock<IOverlayManager>();
        _mockGazeProvider = new Mock<IGazeProvider>();
        _mockMouseMapper = new Mock<IMouseCoordinateMapper>();
        _mockInputSimulator = new Mock<IInputSimulator>();
        _messenger = WeakReferenceMessenger.Default;
        _settings = new EyeCtrlSettings();

        // Create real GazeEngine for integration testing
        _gazeEngine = new GazeEngine(
            _mockGazeProvider.Object,
            _mockMouseMapper.Object,
            _mockInputSimulator.Object,
            _settings,
            NullLogger<GazeEngine>.Instance,
            NullLoggerFactory.Instance);

        _dispatcher = Dispatcher.CurrentDispatcher;
        _settingsService = new SettingsService("test_settings.json", NullLogger<SettingsService>.Instance);

        // Create ViewModels
        _cursorVM = new CursorCircleViewModel(_settings, _messenger);
        _activationVM = new ActivationButtonViewModel(_settings, _messenger);
        _zoomVM = new ZoomOverlayViewModel(_settings, _messenger);
        _zoomIndicatorVM = new ZoomGazeIndicatorViewModel(_settings, _messenger, _dispatcher, _gazeEngine.InteractionManager);
        _actionMenuVM = new ActionMenuViewModel(_settings, _messenger);
        _toolbarVM = new ToolbarViewModel(_settings, _messenger);
        _keyboardVM = new KeyboardViewModel(_settings, _messenger);

        // Setup mock overlay windows
        SetupMockOverlays();

        _coordinator = new EventCoordinator();
    }

    private void SetupMockOverlays()
    {
        _mockOverlayManager.Setup(m => m.CursorCircle).Returns(Mock.Of<CursorCircleWindow>());
        _mockOverlayManager.Setup(m => m.ActivationButton).Returns(Mock.Of<ActivationButtonWindow>());
        _mockOverlayManager.Setup(m => m.ZoomOverlay).Returns(Mock.Of<ZoomOverlayWindow>());
        _mockOverlayManager.Setup(m => m.ZoomSideButtons).Returns(Mock.Of<ZoomSideButtonsWindow>());
        _mockOverlayManager.Setup(m => m.ZoomIndicator).Returns(Mock.Of<ZoomGazeIndicatorWindow>());
        _mockOverlayManager.Setup(m => m.ActionMenu).Returns(Mock.Of<ActionMenuWindow>());
        _mockOverlayManager.Setup(m => m.Toolbar).Returns(Mock.Of<ToolbarWindow>());
        _mockOverlayManager.Setup(m => m.Keyboard).Returns(Mock.Of<KeyboardWindow>());
    }

    [Fact]
    public void Initialize_SubscribesToCursorVMPropertyChanged()
    {
        // Arrange
        var context = CreateContext();

        // Act
        _coordinator.Initialize(context);
        _cursorVM.IsVisible = true;

        // Assert - verify event handler was registered by checking window method was called
        _mockOverlayManager.Verify(m => m.CursorCircle, Times.AtLeastOnce());
    }

    [Fact]
    public void CursorVM_PositionChanged_UpdatesCursorCircleWindow()
    {
        // Arrange
        var mockWindow = new Mock<CursorCircleWindow>();
        _mockOverlayManager.Setup(m => m.CursorCircle).Returns(mockWindow.Object);
        var context = CreateContext();
        _coordinator.Initialize(context);

        var newPosition = new PhysicalPoint(100, 200);

        // Act
        _cursorVM.Position = newPosition;

        // Assert
        mockWindow.Verify(w => w.UpdatePosition(newPosition), Times.Once());
    }

    [Fact]
    public void CursorVM_IsVisibleTrue_ShowsCursorCircleWindow()
    {
        // Arrange
        var mockWindow = new Mock<CursorCircleWindow>();
        _mockOverlayManager.Setup(m => m.CursorCircle).Returns(mockWindow.Object);
        var context = CreateContext();
        _coordinator.Initialize(context);

        // Act
        _cursorVM.IsVisible = true;

        // Assert
        mockWindow.Verify(w => w.Show(), Times.Once());
    }

    [Fact]
    public void CursorVM_IsVisibleFalse_HidesCursorCircleWindow()
    {
        // Arrange
        var mockWindow = new Mock<CursorCircleWindow>();
        _mockOverlayManager.Setup(m => m.CursorCircle).Returns(mockWindow.Object);
        var context = CreateContext();
        _coordinator.Initialize(context);

        // Act
        _cursorVM.IsVisible = false;

        // Assert
        mockWindow.Verify(w => w.Hide(), Times.Once());
    }

    [Fact]
    public void ActivationVM_IsVisibleTrue_ShowsActivationButtonWindow()
    {
        // Arrange
        var mockWindow = new Mock<ActivationButtonWindow>();
        _mockOverlayManager.Setup(m => m.ActivationButton).Returns(mockWindow.Object);
        var context = CreateContext();
        _coordinator.Initialize(context);

        _activationVM.Position = new PhysicalPoint(150, 150);

        // Act
        _activationVM.IsVisible = true;

        // Assert
        mockWindow.Verify(w => w.Show(), Times.Once());
        mockWindow.Verify(w => w.ShowAt(It.IsAny<PhysicalPoint>(), It.IsAny<double>()), Times.Once());
    }

    [Fact]
    public void ActivationVM_DwellProgressChanged_UpdatesActivationButtonWindow()
    {
        // Arrange
        var mockWindow = new Mock<ActivationButtonWindow>();
        _mockOverlayManager.Setup(m => m.ActivationButton).Returns(mockWindow.Object);
        var context = CreateContext();
        _coordinator.Initialize(context);

        // Act
        _activationVM.DwellProgress = 0.5;

        // Assert
        mockWindow.Verify(w => w.UpdateDwellProgress(0.5), Times.Once());
    }

    [Fact]
    public void ZoomVM_IsVisibleTrue_ShowsZoomOverlayWindow()
    {
        // Arrange
        var mockWindow = new Mock<ZoomOverlayWindow>();
        _mockOverlayManager.Setup(m => m.ZoomOverlay).Returns(mockWindow.Object);
        var context = CreateContext();
        _coordinator.Initialize(context);

        _zoomVM.Center = new PhysicalPoint(200, 200);
        _zoomVM.Radius = 100;
        _zoomVM.Magnification = 2.0;
        _zoomVM.GazePoint = new PhysicalPoint(200, 200);

        // Act
        _zoomVM.IsVisible = true;

        // Assert
        mockWindow.Verify(w => w.Show(
            It.IsAny<PhysicalPoint>(),
            It.IsAny<int>(),
            It.IsAny<double>(),
            It.IsAny<PhysicalPoint>()), Times.Once());
    }

    [Fact]
    public void Dispose_UnsubscribesFromPropertyChangedEvents()
    {
        // Arrange
        var mockWindow = new Mock<CursorCircleWindow>();
        _mockOverlayManager.Setup(m => m.CursorCircle).Returns(mockWindow.Object);
        var context = CreateContext();
        _coordinator.Initialize(context);

        // Act
        _coordinator.Dispose();
        _cursorVM.Position = new PhysicalPoint(300, 300);

        // Assert - verify window method was NOT called after disposal
        mockWindow.Verify(w => w.UpdatePosition(It.IsAny<PhysicalPoint>()), Times.Never());
    }

    private EventCoordinationContext CreateContext()
    {
        return new EventCoordinationContext(
            _cursorVM,
            _activationVM,
            _zoomVM,
            _zoomIndicatorVM,
            _actionMenuVM,
            _toolbarVM,
            _keyboardVM,
            _mockOverlayManager.Object,
            _gazeEngine,
            _gazeEngine.InteractionManager,
            _messenger,
            _dispatcher,
            _settingsService,
            _settings,
            _mockInputSimulator.Object);
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _cursorVM.Dispose();
        _activationVM.Dispose();
        _zoomVM.Dispose();
        _actionMenuVM.Dispose();
        _toolbarVM.Dispose();
        _keyboardVM.Dispose();
        _zoomIndicatorVM.Dispose();
        _gazeEngine.Dispose();
        _messenger.Cleanup();
    }
}
