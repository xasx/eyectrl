namespace EyeCtrl.App.Tests.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class ZoomFixationServiceTests
{
    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ZoomGazeIndicatorViewModel _indicatorVM;
    private readonly InteractionManager _interactionManager;
    private readonly ZoomFixationService _service;

    public ZoomFixationServiceTests()
    {
        _messenger = new WeakReferenceMessenger();
        _settings = new EyeCtrlSettings
        {
            Zoom = new ZoomSettings
            {
                CircleRadiusPixels = 200,
                MagnificationLevel = 2.0,
                ZoomGracePeriodMs = 300
            },
            Dwell = new DwellSettings
            {
                FixationRadiusPixels = 60,
                FixationDetectionMs = 800
            }
        };

        // Initialize DpiHelper for tests
        DpiHelper.Initialize(1.0, 1.0);

        // Create a test dispatcher
        _dispatcher = Dispatcher.CurrentDispatcher;

        _indicatorVM = new ZoomGazeIndicatorViewModel(_messenger);
        _interactionManager = new InteractionManager();
        _interactionManager.ZoomEnabled = true;

        _service = new ZoomFixationService(
            _messenger,
            NullLogger<ZoomFixationService>.Instance,
            _settings,
            _dispatcher,
            _interactionManager);
    }

    [Fact]
    public void OnZoomStarted_InitializesFixationDetector()
    {
        // Arrange
        var startMessage = new StartZoomMessage(
            new ScreenPoint(500, 500),
            200,
            2.0,
            new ScreenPoint(500, 500));

        // Act
        _messenger.Send(startMessage);

        // Assert - Service should be active and ready to process gaze
        // We verify this indirectly by sending a gaze message and checking for indicator updates
        var indicatorMessageReceived = false;
        _messenger.Register<UpdateZoomIndicatorPositionMessage>(this, (r, m) =>
        {
            indicatorMessageReceived = true;
        });

        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 500), 1000));

        Assert.True(indicatorMessageReceived);
        _messenger.Unregister<UpdateZoomIndicatorPositionMessage>(this);
    }

    [Fact]
    public void OnGazeMoved_DuringGracePeriod_UpdatesIndicatorPositionOnly()
    {
        // Arrange
        var startMessage = new StartZoomMessage(
            new ScreenPoint(500, 500),
            200,
            2.0,
            new ScreenPoint(500, 500));
        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();

        var positionUpdates = 0;
        var progressUpdates = 0;

        _messenger.Register<UpdateZoomIndicatorPositionMessage>(this, (r, m) => positionUpdates++);
        _messenger.Register<UpdateZoomIndicatorProgressMessage>(this, (r, m) => progressUpdates++);

        // Act - Send gaze during grace period (within 300ms from start)
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(505, 505), 100_000)); // 100ms
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(510, 510), 200_000)); // 200ms

        // Assert - Position should update, but not progress (grace period active)
        Assert.True(positionUpdates > 0, "Indicator position should be updated during grace period");
        Assert.Equal(0, progressUpdates); // No progress updates during grace period

        _messenger.Unregister<UpdateZoomIndicatorPositionMessage>(this);
        _messenger.Unregister<UpdateZoomIndicatorProgressMessage>(this);
    }

    [Fact]
    public void OnGazeMoved_OutsideBounds_ResetsFixation()
    {
        // Arrange
        var startMessage = new StartZoomMessage(
            new ScreenPoint(500, 500),
            200,
            2.0,
            new ScreenPoint(500, 500));
        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();

        var resetMessageReceived = false;
        _messenger.Register<ResetZoomIndicatorMessage>(this, (r, m) =>
        {
            resetMessageReceived = true;
        });

        // Act - First gaze starts grace period, second gaze is outside bounds after grace period
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 500), 100_000));
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(800, 800), 500_000)); // 400ms after grace start, outside bounds

        // Assert - Reset message should be sent
        Assert.True(resetMessageReceived, "Fixation should be reset when gaze moves outside bounds");

        _messenger.Unregister<ResetZoomIndicatorMessage>(this);
    }

    [Fact]
    public void OnGazeMoved_WithinBounds_AfterGracePeriod_UpdatesProgress()
    {
        // Arrange
        var startMessage = new StartZoomMessage(
            new ScreenPoint(500, 500),
            200,
            2.0,
            new ScreenPoint(500, 500));
        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();

        var progressUpdateReceived = false;
        _messenger.Register<UpdateZoomIndicatorProgressMessage>(this, (r, m) =>
        {
            progressUpdateReceived = true;
        });

        // Act - Wait for grace period, then send gaze within bounds
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 500), 100_000)); // Start grace period
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(505, 505), 400_000)); // After grace period (400ms)

        // Assert
        Assert.True(progressUpdateReceived, "Progress should be updated after grace period with valid gaze");

        _messenger.Unregister<UpdateZoomIndicatorProgressMessage>(this);
    }

    [Fact]
    public void OnFixationCompleted_EmitsCompletionMessage()
    {
        // Arrange
        var startMessage = new StartZoomMessage(
            new ScreenPoint(500, 500),
            200,
            2.0,
            new ScreenPoint(500, 500));
        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();

        ZoomFixationCompletedMessage? completionMessage = null;
        _messenger.Register<ZoomFixationCompletedMessage>(this, (r, m) =>
        {
            completionMessage = m;
        });

        // Act - Simulate fixation completion by sending steady gaze for required duration
        // Need: 300ms grace + 800ms fixation = 1100ms total
        var fixationPoint = new ScreenPoint(510, 510);
        var startTime = 0L;

        for (int i = 0; i < 35; i++) // Send gaze at ~30Hz for 1.167 seconds
        {
            var timestamp = startTime + (i * 33_333); // 33ms intervals
            _messenger.Send(new GazeCursorMovedMessage(fixationPoint, timestamp));
        }

        // Assert
        Assert.NotNull(completionMessage);
        Assert.Equal(fixationPoint.X, completionMessage.PrecisePoint.X, 2); // Allow small tolerance
        Assert.Equal(fixationPoint.Y, completionMessage.PrecisePoint.Y, 2);

        _messenger.Unregister<ZoomFixationCompletedMessage>(this);
    }

    [Fact]
    public void OnZoomEnded_CleansUpResources()
    {
        // Arrange
        var startMessage = new StartZoomMessage(
            new ScreenPoint(500, 500),
            200,
            2.0,
            new ScreenPoint(500, 500));
        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();

        var positionUpdateCount = 0;
        _messenger.Register<UpdateZoomIndicatorPositionMessage>(this, (r, m) => positionUpdateCount++);

        // Confirm service is active
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(505, 505), 100_000));
        Assert.True(positionUpdateCount > 0, "Service should be active before zoom ends");

        // Act - End zoom
        _messenger.Send(new EndZoomMessage());

        // Reset counter
        positionUpdateCount = 0;

        // Send more gaze messages
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(510, 510), 200_000));

        // Assert - No more messages should be processed
        Assert.Equal(0, positionUpdateCount);

        _messenger.Unregister<UpdateZoomIndicatorPositionMessage>(this);
    }

    [Fact]
    public void Dispose_UnregistersFromMessenger()
    {
        // Arrange
        var startMessage = new StartZoomMessage(
            new ScreenPoint(500, 500),
            200,
            2.0,
            new ScreenPoint(500, 500));
        _messenger.Send(startMessage);

        // Act
        _service.Dispose();

        // Assert - Sending gaze message should not cause any updates
        var messageReceived = false;
        _messenger.Register<UpdateZoomIndicatorPositionMessage>(this, (r, m) => messageReceived = true);

        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 500), 100_000));

        Assert.False(messageReceived, "Service should not process messages after disposal");

        _messenger.Unregister<UpdateZoomIndicatorPositionMessage>(this);
    }

    [Fact]
    public void OnGazeMoved_WhenNotInZoomState_IgnoresGaze()
    {
        // Arrange
        var startMessage = new StartZoomMessage(
            new ScreenPoint(500, 500),
            200,
            2.0,
            new ScreenPoint(500, 500));
        _messenger.Send(startMessage);
        // Don't call _interactionManager.OnZoom() - keep state as FreeLook

        var messageReceived = false;
        _messenger.Register<UpdateZoomIndicatorPositionMessage>(this, (r, m) => messageReceived = true);

        // Act
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 500), 100_000));

        // Assert - No messages should be sent when not in zoom state
        Assert.False(messageReceived, "Service should ignore gaze when not in ZoomSelectionActive state");

        _messenger.Unregister<UpdateZoomIndicatorPositionMessage>(this);
    }
}
