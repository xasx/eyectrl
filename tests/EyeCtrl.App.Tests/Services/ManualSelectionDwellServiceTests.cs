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
/// Defines the App-layer test collection that isolates DpiHelper's process-global scale.
///
/// Membership rule: any class that CALLS DpiHelper.Initialize. xUnit v3 gives a
/// DisableParallelization collection an EXCLUSIVE phase — it runs concurrently with no other
/// collection, parallel or non-parallel — so confining the writers here is sufficient on its
/// own: while a writer runs nothing else runs, and no reader can observe the scale change
/// mid-test. DpiHelperWriterIsolationTests enforces that rule across every test project;
/// NonParallelCollectionPin pins the xUnit scheduling behaviour it rests on.
///
/// Readers MAY join but need not. Five of the classes here are pure readers, kept because the
/// cost is nil (all ten together run in about a second) — belt and braces, not load-bearing.
///
/// Correction, because this doc previously said the opposite: it stated that
/// "DisableParallelization only serialises within a collection" — xUnit v2 wording — and drew
/// from that a cross-collection exposure for seven reader classes that xUnit allows only one
/// collection each and that another collection had already claimed:
///   WpfApplication (Application singleton) - EyeCareHooksTests (UI-only) and
///     ToolbarActivationFlash/ButtonVisual/Orientation/TargetPlacementTests, which run headless
///     and build a ToolbarViewModel (its ctor reads DpiHelper) via
///     ToolbarTestHelpers.NewBoundToolbar.
///   ExternalDwellPipe - ExternalDwellService/WireContractTests, also headless, which reach
///     DwellFeedbackOverlay.ShowAt through SetFeedback when dwell progresses on a live target.
/// Both of those collections are themselves DisableParallelization, so under v3 none of the
/// seven ever overlapped a writer and none was ever exposed. Neither the merged collection nor
/// the injectable-DPI refactor that the old wording implied is needed. Keep the reach paths
/// above: they are still the map of who touches the scale, and still correct.
///
/// What exclusivity does NOT give you is ordering. A writer that leaks a non-1.0 scale still
/// changes what a later collection reads, because the order between non-parallel collections is
/// not contractual. Every writer here restores 1.0 in a Dispose or a finally — keep it so.
///
/// Naming a type is NOT enough to qualify — the source-census tests match filenames like
/// "SettingsWindow.xaml" as text, and several helpers (EyeCareBannerWindow.ComputeDwellGeometry,
/// OverlayPositioning.PlaceAtEdge) take the scale as a parameter rather than reading the global.
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
        _interactionManager.SelectionMode = SelectionMode.ManualSelection;

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

        Check.That(indicatorMessageReceived).IsTrue();
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
        Check.That(positionUpdates > 0).IsTrue();
        Check.That(progressUpdates).IsEqualTo(0); // No progress updates during grace period

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
        Check.That(resetMessageReceived).IsTrue();

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

        // Act - Start the grace period, then send two gazes within bounds after it.
        // The dwell detector only *enters* the target on the first post-grace sample; a
        // second sample is required before it emits a progress update.
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 500), 100_000)); // Start grace period
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(505, 505), 400_000)); // After grace period — dwell begins
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(505, 505), 600_000)); // Dwell advances → progress emitted

        // Assert
        Check.That(progressUpdateReceived).IsTrue();

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
        Check.That(completionMessage).IsNotNull();
        Check.That(completionMessage!.PrecisePoint.X).IsCloseTo(dwellPoint.X, 0.005); // Allow small tolerance
        Check.That(completionMessage!.PrecisePoint.Y).IsCloseTo(dwellPoint.Y, 0.005);

        _messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);
    }

    [Fact]
    public void HeldSamples_DoNotCompleteManualSelectionDwell()
    {
        // Same stream as the completion test above, but every sample is gap-held: a blink freezes the
        // published position inside the manual-selection circle, and feeding it would commit the click.
        var startMessage = new StartManualSelectionMessage(
            new PhysicalPoint(500, 500),
            200,
            2.0,
            new PhysicalPoint(500, 500));
        _messenger.Send(startMessage);
        _interactionManager.OnFixationStarted(startMessage.GazePoint);
        _interactionManager.OnActivationButtonDwelled();

        ManualSelectionDwellCompletedMessage? completionMessage = null;
        _messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) => completionMessage = m);

        var dwellPoint = new PhysicalPoint(510, 510);
        for (int i = 0; i < 40; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new GazeFrame(dwellPoint, i * 33_333, IsInGap: true)));
        }

        Check.That(completionMessage).IsNull();

        _messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);
    }

    [Fact]
    public void Recapture_ResetsGraceAndDwellProgress_ButKeepsTheSessionAlive()
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
        _messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) => completionMessage = m);

        var dwellPoint = new PhysicalPoint(510, 510);
        long ts = 0;
        const long stepUs = 33_333;

        void Feed(int samples)
        {
            for (int i = 0; i < samples; i++)
            {
                ts += stepUs;
                _messenger.Send(new GazeCursorMovedMessage(dwellPoint, ts));
            }
        }

        // Cross the 300ms grace period and build real progress toward the 800ms dwell — about
        // 667ms elapsed in total, comfortably under the ~1100ms (grace + dwell) it would take an
        // uninterrupted dwell to complete.
        Feed(20);
        Check.That(completionMessage).IsNull();

        // Act — recapture: grace and dwell progress must both restart from here.
        _messenger.Send(new ManualSelectionRecaptureMessage());

        // Assert — feed enough MORE samples that total elapsed since the ORIGINAL start now
        // exceeds the ~1100ms an uninterrupted dwell would need (667ms + 533ms ≈ 1200ms), while
        // elapsed since the RECAPTURE stays well short of a fresh 1100ms cycle. Completing here
        // would mean the pre-recapture progress silently survived the reset.
        Feed(16);
        Check.That(completionMessage).IsNull();

        // Assert — a full fresh grace+dwell cycle from the recapture must still be able to
        // complete: the reset must not have torn down or otherwise disabled the session.
        Feed(20);
        Check.That(completionMessage).IsNotNull();

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
        Check.That(positionUpdateCount > 0).IsTrue();

        // Act - End ManualSelection
        _messenger.Send(new EndManualSelectionMessage());

        // Reset counter
        positionUpdateCount = 0;

        // Send more gaze messages
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(510, 510), 200_000));

        // Assert - No more messages should be processed
        Check.That(positionUpdateCount).IsEqualTo(0);

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

        Check.That(messageReceived).IsFalse();

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
        Check.That(messageReceived).IsFalse();

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

        // Configure the indicator VM so it can position the dot at the gaze point.
        _indicatorVM.Configure(_settings);

        UpdateManualSelectionIndicatorPositionMessage? positionMessage = null;
        _messenger.Register<UpdateManualSelectionIndicatorPositionMessage>(this, (r, m) => positionMessage = m);

        // Act - Gaze moves slightly right and down in physical screen space
        var newGazePoint = new PhysicalPoint(510, 510); // 10px right, 10px down from center
        _messenger.Send(new GazeCursorMovedMessage(newGazePoint, 100_000));

        // Assert - The position message should contain the physical screen coordinates, not magnified
        Check.That(positionMessage).IsNotNull();
        Check.That(positionMessage!.GazePoint.X).IsCloseTo(510, 0.005);
        Check.That(positionMessage!.GazePoint.Y).IsCloseTo(510, 0.005);
    
        // The indicator is drawn at the RAW gaze point — where the user is looking inside the already-
        // zoomed circle, which is the exact screen point the dwell selects. It is NOT offset by the
        // magnification (that older mapping drifted the dot off the gaze), so it equals the physical
        // screen coordinate, matching this test's name.
        Check.That(_indicatorVM.IndicatorX).IsCloseTo(510, 0.005);
        Check.That(_indicatorVM.IndicatorY).IsCloseTo(510, 0.005);

        _messenger.Unregister<UpdateManualSelectionIndicatorPositionMessage>(this);
    }
}
