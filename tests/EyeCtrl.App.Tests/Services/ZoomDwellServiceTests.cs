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

/// <summary>
/// Defines a test collection that disables parallelization for App layer tests using DpiHelper.
/// </summary>
[CollectionDefinition("DpiHelper Tests (App)", DisableParallelization = true)]
public class AppDpiHelperTestCollection
{
}

/// <summary>
/// Tests for ManualSelectionDwellService.
/// Part of the non-parallelized collection because it uses DpiHelper which has global static state.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ManualSelectionDwellServiceTests
{
    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ManualSelectionGazeIndicatorViewModel _indicatorVM;
    private readonly InteractionManager _interactionManager;
    private readonly ManualSelectionDwellService _service;

    public ManualSelectionDwellServiceTests()
    {
        _messenger = new WeakReferenceMessenger();
        _settings = new EyeCtrlSettings
        {
            ManualSelection = new ManualSelectionSettings
            {
                CircleRadiusPixels = 200,
                MagnificationLevel = 2.0,
                ManualSelectionGracePeriodMs = 300
            },
            Dwell = new DwellSettings
            {
                FixationRadiusPixels = 60,
                ManualSelectionActionMenuDwellMs = 800  // Dwell time for ManualSelection selection
            }
        };

        // Initialize DpiHelper for tests
        DpiHelper.Initialize(1.0, 1.0);

        // Create a test dispatcher
        _dispatcher = Dispatcher.CurrentDispatcher;

        _indicatorVM = new ManualSelectionGazeIndicatorViewModel(_messenger);
        _interactionManager = new InteractionManager();
        _interactionManager.ManualSelectionEnabled = true;

        _service = new ManualSelectionDwellService(
            _messenger,
            NullLogger<ManualSelectionDwellService>.Instance,
            _settings,
            _dispatcher,
            _interactionManager);
    }

    [Fact]
    public void OnManualSelectionStarted_InitializesDwellDetector()
    {
        // Arrange
        var startMessage = new StartManualSelectionMessage(
            new PhysicalPoint(500, 500),
            200,
            2.0,
            new PhysicalPoint(500, 500));

        // Act
        _messenger.Send(startMessage);

        // Assert - Service should be active and ready to process gaze
        // We verify this indirectly by sending a gaze message and checking for indicator updates
        var indicatorMessageReceived = false;
        _messenger.Register<UpdateManualSelectionIndicatorPositionMessage>(this, (r, m) =>
        {
            indicatorMessageReceived = true;
        });

        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 500), 1000));

        Assert.True(indicatorMessageReceived);
        _messenger.Unregister<UpdateManualSelectionIndicatorPositionMessage>(this);
    }

    [Fact]
    public void OnGazeMoved_DuringGracePeriod_UpdatesIndicatorPositionOnly()
    {
        // Arrange
        var startMessage = new StartManualSelectionMessage(
            new PhysicalPoint(500, 500),
            200,
            2.0,
            new PhysicalPoint(500, 500));
        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();

        var positionUpdates = 0;
        var progressUpdates = 0;

        _messenger.Register<UpdateManualSelectionIndicatorPositionMessage>(this, (r, m) => positionUpdates++);
        _messenger.Register<UpdateManualSelectionIndicatorProgressMessage>(this, (r, m) => progressUpdates++);

        // Act - Send gaze during grace period (within 300ms from start)
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(505, 505), 100_000)); // 100ms
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(510, 510), 200_000)); // 200ms

        // Assert - Position should update, but not progress (grace period active)
        Assert.True(positionUpdates > 0, "Indicator position should be updated during grace period");
        Assert.Equal(0, progressUpdates); // No progress updates during grace period

        _messenger.Unregister<UpdateManualSelectionIndicatorPositionMessage>(this);
        _messenger.Unregister<UpdateManualSelectionIndicatorProgressMessage>(this);
    }

    [Fact]
    public void OnGazeMoved_OutsideBounds_ResetsFixation()
    {
        // Arrange
        var startMessage = new StartManualSelectionMessage(
            new PhysicalPoint(500, 500),
            200,
            2.0,
            new PhysicalPoint(500, 500));
        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();

        var resetMessageReceived = false;
        _messenger.Register<ResetManualSelectionIndicatorMessage>(this, (r, m) =>
        {
            resetMessageReceived = true;
        });

        // Act - First gaze starts grace period, second gaze is outside bounds after grace period
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 500), 100_000));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(800, 800), 500_000)); // 400ms after grace start, outside bounds

        // Assert - Reset message should be sent
        Assert.True(resetMessageReceived, "Dwell should be reset when gaze moves outside bounds");

        _messenger.Unregister<ResetManualSelectionIndicatorMessage>(this);
    }

    [Fact]
    public void OnGazeMoved_WithinBounds_AfterGracePeriod_UpdatesProgress()
    {
        // Arrange
        var startMessage = new StartManualSelectionMessage(
            new PhysicalPoint(500, 500),
            200,
            2.0,
            new PhysicalPoint(500, 500));
        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();

        var progressUpdateReceived = false;
        _messenger.Register<UpdateManualSelectionIndicatorProgressMessage>(this, (r, m) =>
        {
            progressUpdateReceived = true;
        });

        // Act - Wait for grace period, then send gaze within bounds
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 500), 100_000)); // Start grace period
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(505, 505), 400_000)); // After grace period (400ms)

        // Assert
        Assert.True(progressUpdateReceived, "Progress should be updated after grace period with valid gaze");

        _messenger.Unregister<UpdateManualSelectionIndicatorProgressMessage>(this);
    }

    [Fact]
    public void OnDwellCompleted_EmitsCompletionMessage()
    {
        // Arrange
        var startMessage = new StartManualSelectionMessage(
            new PhysicalPoint(500, 500),
            200,
            2.0,
            new PhysicalPoint(500, 500));
        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();

        ManualSelectionDwellCompletedMessage? completionMessage = null;
        _messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) =>
        {
            completionMessage = m;
        });

        // Act - Simulate dwell completion by sending steady gaze for required duration
        // Need: 300ms grace + 800ms dwell = 1100ms total
        var dwellPoint = new PhysicalPoint(510, 510);
        var startTime = 0L;

        for (int i = 0; i < 40; i++) // Send gaze at ~30Hz for 1.33 seconds (sufficient for 1100ms)
        {
            var timestamp = startTime + (i * 33_333); // 33ms intervals
            _messenger.Send(new GazeCursorMovedMessage(dwellPoint, timestamp));
        }

        // Assert
        Assert.NotNull(completionMessage);
        Assert.Equal(dwellPoint.X, completionMessage.PrecisePoint.X, 2); // Allow small tolerance
        Assert.Equal(dwellPoint.Y, completionMessage.PrecisePoint.Y, 2);

        _messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);
    }

    [Fact]
    public void OnManualSelectionEnded_CleansUpResources()
    {
        // Arrange
        var startMessage = new StartManualSelectionMessage(
            new PhysicalPoint(500, 500),
            200,
            2.0,
            new PhysicalPoint(500, 500));
        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();

        var positionUpdateCount = 0;
        _messenger.Register<UpdateManualSelectionIndicatorPositionMessage>(this, (r, m) => positionUpdateCount++);

        // Confirm service is active
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(505, 505), 100_000));
        Assert.True(positionUpdateCount > 0, "Service should be active before ManualSelection ends");

        // Act - End ManualSelection
        _messenger.Send(new EndManualSelectionMessage());

        // Reset counter
        positionUpdateCount = 0;

        // Send more gaze messages
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(510, 510), 200_000));

        // Assert - No more messages should be processed
        Assert.Equal(0, positionUpdateCount);

        _messenger.Unregister<UpdateManualSelectionIndicatorPositionMessage>(this);
    }

    [Fact]
    public void Dispose_UnregistersFromMessenger()
    {
        // Arrange
        var startMessage = new StartManualSelectionMessage(
            new PhysicalPoint(500, 500),
            200,
            2.0,
            new PhysicalPoint(500, 500));
        _messenger.Send(startMessage);

        // Act
        _service.Dispose();

        // Assert - Sending gaze message should not cause any updates
        var messageReceived = false;
        _messenger.Register<UpdateManualSelectionIndicatorPositionMessage>(this, (r, m) => messageReceived = true);

        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 500), 100_000));

        Assert.False(messageReceived, "Service should not process messages after disposal");

        _messenger.Unregister<UpdateManualSelectionIndicatorPositionMessage>(this);
    }

    [Fact]
    public void OnGazeMoved_WhenNotInManualSelectionState_IgnoresGaze()
    {
        // Arrange
        var startMessage = new StartManualSelectionMessage(
            new PhysicalPoint(500, 500),
            200,
            2.0,
            new PhysicalPoint(500, 500));
        _messenger.Send(startMessage);
        // Don't call _interactionManager.OnManualSelection() - keep state as FreeLook

        var messageReceived = false;
        _messenger.Register<UpdateManualSelectionIndicatorPositionMessage>(this, (r, m) => messageReceived = true);

        // Act
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 500), 100_000));

        // Assert - No messages should be sent when not in ManualSelection state
        Assert.False(messageReceived, "Service should ignore gaze when not in ManualSelectionActive state");

        _messenger.Unregister<UpdateManualSelectionIndicatorPositionMessage>(this);
    }

    [Fact]
    public void IndicatorPosition_ShouldBeInPhysicalScreenCoordinates()
    {
        // Arrange - Start ManualSelection at screen point (500, 500) with 2x magnification
        var ManualSelectionCenter = new PhysicalPoint(500, 500);
        var gazePoint = new PhysicalPoint(500, 500);
        var startMessage = new StartManualSelectionMessage(ManualSelectionCenter, 200, 2.0, gazePoint);

        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(gazePoint);
        _interactionManager.OnActivationButtonDwelled();

        // The ViewModel should convert gaze coordinates to magnified space
        _indicatorVM.Configure(_settings);

        UpdateManualSelectionIndicatorPositionMessage? positionMessage = null;
        _messenger.Register<UpdateManualSelectionIndicatorPositionMessage>(this, (r, m) => positionMessage = m);

        // Act - Gaze moves slightly right and down in physical screen space
        var newGazePoint = new PhysicalPoint(510, 510); // 10px right, 10px down from center
        _messenger.Send(new GazeCursorMovedMessage(newGazePoint, 100_000));

        // Assert - The position message should contain the physical screen coordinates, not magnified
        Assert.NotNull(positionMessage);
        Assert.Equal(510, positionMessage.GazePoint.X, 2);
        Assert.Equal(510, positionMessage.GazePoint.Y, 2);

        // The ViewModel should convert this to magnified coordinates
        // magnifiedX = ManualSelectionCenter.X + (gazePoint.X - originalCenter.X) * magnification
        // magnifiedX = 500 + (510 - 500) * 2.0 = 500 + 20 = 520
        Assert.Equal(520, _indicatorVM.IndicatorX, 2);
        Assert.Equal(520, _indicatorVM.IndicatorY, 2);

        _messenger.Unregister<UpdateManualSelectionIndicatorPositionMessage>(this);
    }
}
