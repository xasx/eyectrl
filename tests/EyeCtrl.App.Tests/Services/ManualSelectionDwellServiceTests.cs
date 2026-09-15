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

        // Act - Start the grace period, then seat the anchor with five stable post-grace samples
        // (stability onset is K=5), then advance the dwell so progress is emitted.
        // The dwell detector only *enters* the target on the sample that seats the anchor; a
        // further sample is required before it emits a progress update.
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 500), 100_000)); // Start grace period
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(505, 505), 400_000)); // After grace — stability window opens
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(505, 505), 433_333));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(505, 505), 466_666));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(505, 505), 500_000));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(505, 505), 533_333)); // K-th sample seats the anchor → dwell begins
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(505, 505), 566_666)); // Dwell advances → progress emitted

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

        // Act - Simulate dwell completion by sending steady gaze for required duration.
        // Need: 300ms grace + 5-sample stability onset (~167ms) + 800ms dwell ≈ 1.27s.
        var dwellPoint = new PhysicalPoint(510, 510);
        var startTime = 0L;

        for (int i = 0; i < 45; i++) // Send gaze at ~30Hz for 1.5 seconds (sufficient for ~1.27s)
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

    /// <summary>
    /// The post-merge defect found when this ticket's fix met ticket 4's commit-rule state
    /// (anchor, held samples, running commit centroid — none of which existed when this test was
    /// first written): a recapture must not let a commit centroid seeded before the recapture
    /// survive into the commit that fires after it, and the stale anchor mark must be released
    /// rather than left pointing at content that no longer exists.
    /// <para>
    /// D is deliberately chosen INSIDE the stale anchor's 72 px hold radius, not outside it. A
    /// point outside the stale hold radius would trip the pre-existing "sustained out-of-hold"
    /// self-heal (<see cref="ResetDwell"/> via <c>HoldOutlierSkipLimit</c>) after a few samples
    /// regardless of whether recapture itself clears anything — passing even with
    /// <c>ClearCommitState()</c> removed from <c>OnRecapture</c>, which defeats the point of this
    /// test. Staying inside the stale hold radius means a leaked anchor is never challenged by that
    /// other mechanism, so only OnRecapture's own reset can be what clears it.
    /// </para>
    /// Uses the StartSession/SendGaze helpers and 50 ms sample spacing the targeting probes below
    /// already establish.
    /// </summary>
    [Fact]
    public void Recapture_DoesNotLetAPreRecaptureCommitCentroidSurviveIntoTheNextCommit()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        ManualSelectionDwellCompletedMessage? completed = null;
        _messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) => completed = m);

        var anchorReleased = false;
        _messenger.Register<ManualSelectionAnchorChangedMessage>(this, (r, m) =>
        {
            if (m.Anchor is null) anchorReleased = true;
        });

        // Seat an anchor at A=(500,500): six samples inside the 300ms grace, then five more that
        // seat it (mirrors OnGazeMoved_StabilityOnset_SeatsAnchorAtClusterCentroid).
        var anchorPoint = new PhysicalPoint(500, 500);
        SendGazeRun(anchorPoint, FirstPostGraceSampleIndex);
        for (var i = FirstPostGraceSampleIndex; i < FirstPostGraceSampleIndex + 5; i++)
            SendGaze(anchorPoint, i);

        // Pull the commit centroid toward B=(540,500) — inside the 72 px hold radius, so these
        // samples accumulate into the running commit average rather than resetting the dwell.
        // 5 samples * 50ms = 250ms of dwell elapsed, comfortably under the 800ms requirement, so
        // this must not have completed yet.
        var driftPoint = new PhysicalPoint(540, 500);
        var nextIndex = FirstPostGraceSampleIndex + 5;
        for (var i = nextIndex; i < nextIndex + 5; i++)
            SendGaze(driftPoint, i);
        nextIndex += 5;

        Check.WithCustomMessage("Test setup must not have already completed before the recapture.")
            .That(completed).IsNull();

        // Act — recapture: the anchor and its commit centroid (seeded near A/B) must not survive.
        _messenger.Send(new ManualSelectionRecaptureMessage());

        Check.WithCustomMessage(
                "The stale anchor mark must be released on recapture, not left pointing at content "
                + "that no longer exists.")
            .That(anchorReleased).IsTrue();

        // Feed a fresh point D — 40 px from the STALE anchor A, inside its 72 px hold radius — until
        // a fresh grace + stability-onset + dwell cycle completes. If the anchor/commit state had
        // not been cleared, D would read as "still within the old hold" and accumulate onto the
        // stale A/B sum rather than seating and committing fresh.
        var freshPoint = new PhysicalPoint(500, 540);
        for (var i = nextIndex; i < nextIndex + 80 && completed is null; i++)
            SendGaze(freshPoint, i);

        Check.WithCustomMessage("A full fresh cycle after the recapture must still be able to complete.")
            .That(completed).IsNotNull();

        // The commit must be seeded ONLY from post-recapture samples at D — not blended with the
        // A/B centroid the pre-recapture drift built up (a stale, un-cleared _commitSumX/Y would
        // pull this off (500, 540) by several pixels, since D falls inside the stale hold radius and
        // would silently accumulate onto it instead of starting fresh).
        Check.That(completed!.PrecisePoint.X).IsCloseTo(500, 0.01);
        Check.That(completed!.PrecisePoint.Y).IsCloseTo(540, 0.01);

        _messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);
        _messenger.Unregister<ManualSelectionAnchorChangedMessage>(this);
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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Targeting unit: the visible-rim boundary, the anchored hold and the centroid commit.
    //
    // These probes are deliberately discriminating. Under the old rule the dwell target was
    // re-centred on the live gaze every sample, so the distance to the hit-tested point was
    // identically zero: the boundary, the radius and the commit point all had no observable
    // effect and the whole suite stayed green. Each test below is paired with a mutation in the
    // fix it pins — reverting that fix turns the test red.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sample spacing for the targeting probes: 50 ms at ~20 Hz.</summary>
    private const long TargetingSampleSpacingUs = 50_000;

    /// <summary>Grace is 300 ms, so samples 0–5 are inside it and sample 6 is the first post-grace sample.</summary>
    private const int FirstPostGraceSampleIndex = 6;

    private void StartSession(PhysicalPoint center, PhysicalPoint gazePoint)
    {
        _messenger.Send(new StartManualSelectionMessage(center, 200, 2.0, gazePoint));
        _interactionManager.OnFixationStarted(gazePoint);
        _interactionManager.OnActivationButtonDwelled();
    }

    private void SendGaze(PhysicalPoint point, int sampleIndex)
        => _messenger.Send(new GazeCursorMovedMessage(point, sampleIndex * TargetingSampleSpacingUs));

    private void SendGazeRun(PhysicalPoint point, int count)
    {
        for (var i = 0; i < count; i++)
            SendGaze(point, i);
    }

    /// <summary>
    /// Boundary probe at distance 300 px: outside the OLD dead ring (radius x ScaleX = 200) and
    /// inside the visible rim (radius x magnification x ScaleX = 400). Reverting the boundary
    /// expression to the old one makes this reset.
    /// </summary>
    [Fact]
    public void OnGazeMoved_InsideVisibleRimOutsideDeadRing_DoesNotReset()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var resets = 0;
        _messenger.Register<ResetManualSelectionIndicatorMessage>(this, (r, m) => resets++);

        SendGazeRun(new PhysicalPoint(800, 500), 14);

        Check.WithCustomMessage(
                "The boundary the user can see is the boundary that decides: a gaze inside the "
                + "visible rim must not reset, even outside the old radius x ScaleX dead ring.")
            .That(resets).IsEqualTo(0);

        _messenger.Unregister<ResetManualSelectionIndicatorMessage>(this);
    }

    /// <summary>Upper bound of the same invariant: beyond the visible rim the dwell still resets.</summary>
    [Fact]
    public void OnGazeMoved_OutsideVisibleRim_Resets()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var resets = 0;
        _messenger.Register<ResetManualSelectionIndicatorMessage>(this, (r, m) => resets++);

        SendGazeRun(new PhysicalPoint(950, 500), 14); // distance 450 > 400 px rim

        Check.WithCustomMessage("A gaze outside the visible rim must reset.")
            .That(resets).IsStrictlyGreaterThan(0);

        _messenger.Unregister<ResetManualSelectionIndicatorMessage>(this);
    }

    /// <summary>
    /// The visible rim is a physical-pixel radius, so it must carry DpiHelper.ScaleX: at 150 %
    /// scaling a 450 px offset is inside the 600 px rim. Dropping ScaleX from the boundary
    /// expression (the version that passes every other test) makes this reset.
    /// </summary>
    [Fact]
    public void OnGazeMoved_VisibleRim_ScalesWithDisplayDpi()
    {
        DpiHelper.Initialize(1.5, 1.5);
        try
        {
            StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

            var resets = 0;
            _messenger.Register<ResetManualSelectionIndicatorMessage>(this, (r, m) => resets++);

            SendGazeRun(new PhysicalPoint(950, 500), 14); // 450 px: inside 200 x 2.0 x 1.5 = 600

            Check.WithCustomMessage(
                    "At 150 % scaling the visible rim is 600 physical px; a 450 px offset is inside it. "
                    + "A boundary missing ScaleX would gate at 400 and reset.")
                .That(resets).IsEqualTo(0);

            _messenger.Unregister<ResetManualSelectionIndicatorMessage>(this);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    /// <summary>
    /// The user's reported symptom: a dwell anywhere in the visible circle must complete, and at
    /// the point aimed at. Under the old boundary this probe (300 px of the 400 px rim) resets
    /// forever and never completes.
    /// </summary>
    [Fact]
    public void OnDwellCompleted_NearTheVisibleRim_CommitsAtTheAimedPoint()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        ManualSelectionDwellCompletedMessage? completed = null;
        _messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) => completed = m);

        var point = new PhysicalPoint(800, 500);
        for (var i = 0; i < 60 && completed is null; i++)
            SendGaze(point, i);

        Check.WithCustomMessage("A dwell inside the visible rim must complete.")
            .That(completed).IsNotNull();
        Check.That(completed!.PrecisePoint.X).IsCloseTo(800, 0.01);
        Check.That(completed!.PrecisePoint.Y).IsCloseTo(500, 0.01);

        _messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);
    }

    /// <summary>
    /// Committing at the running centroid, not the last sample. The stream holds at the anchor and
    /// then sits 60 px off it (still inside the 72 px hold); the commit must land on the mean of the
    /// held samples. Committing at the last gaze point misses by ~37 px and turns this red.
    /// </summary>
    [Fact]
    public void OnDwellCompleted_CommitsAtRunningCentroid_NotLastSample()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        ManualSelectionDwellCompletedMessage? completed = null;
        _messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) => completed = m);

        double sumX = 0, sumY = 0;
        var count = 0;
        PhysicalPoint last = default;
        for (var i = 0; i < 60 && completed is null; i++)
        {
            // p0–p18 at the anchor, then 60 px off it (inside the hold radius) for the tail.
            var point = i < 19 ? new PhysicalPoint(500, 500) : new PhysicalPoint(560, 500);
            if (i >= FirstPostGraceSampleIndex)
            {
                sumX += point.X;
                sumY += point.Y;
                count++;
            }
            last = point;
            SendGaze(point, i);
        }

        Check.That(completed).IsNotNull();
        var expectedX = sumX / count;
        var expectedY = sumY / count;
        Check.That(completed!.PrecisePoint.X).IsCloseTo(expectedX, 0.01);
        Check.That(completed!.PrecisePoint.Y).IsCloseTo(expectedY, 0.01);

        Check.WithCustomMessage("The probe must discriminate: the last sample sits far from the centroid.")
            .That(Math.Abs(last.X - expectedX)).IsStrictlyGreaterThan(30);

        _messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);
    }

    /// <summary>
    /// The hold gate itself: a gaze that never settles can never seat an anchor, so it never
    /// commits. Under the old follow-gaze rule the dwell completes on this stream.
    /// </summary>
    [Fact]
    public void OnGazeMoved_GazeMovingOutsideHoldRadius_DoesNotComplete()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var completed = false;
        _messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) => completed = true);

        // Alternating 200 px apart: at most 4 of any 7-sample window can be within the onset
        // window, so K=5 stability never onsets and no anchor ever seats.
        for (var i = 0; i < 80; i++)
            SendGaze(i % 2 == 0 ? new PhysicalPoint(400, 500) : new PhysicalPoint(600, 500), i);

        Check.WithCustomMessage("A gaze that never stabilises must not commit.")
            .That(completed).IsFalse();

        _messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);
    }

    /// <summary>
    /// Skip-N: one isolated out-of-hold sample must not break an in-flight dwell, and must not
    /// release the anchor. Treating any out-of-hold sample as gaze-left releases the anchor and
    /// delays completion past the probe.
    /// </summary>
    [Fact]
    public void OnGazeMoved_IsolatedOutlierSample_DoesNotBreakDwell()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var completed = false;
        var anchorReleases = 0;
        _messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) => completed = true);
        _messenger.Register<ManualSelectionAnchorChangedMessage>(this, (r, m) =>
        {
            if (m.Anchor is null) anchorReleases++;
        });

        // Seat at sample 10 (first five post-grace samples), dwell completes at sample 26; one
        // isolated jump to 200 px off the anchor at sample 13 is skipped.
        for (var i = 0; i < 31; i++)
            SendGaze(i == 13 ? new PhysicalPoint(700, 500) : new PhysicalPoint(500, 500), i);

        Check.WithCustomMessage("An isolated outlier must not break the dwell.").That(completed).IsTrue();
        Check.WithCustomMessage("An isolated outlier must not release the anchor.").That(anchorReleases).IsEqualTo(0);

        _messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);
        _messenger.Unregister<ManualSelectionAnchorChangedMessage>(this);
    }

    /// <summary>
    /// The other half of skip-N: more consecutive out-of-hold samples than the skip limit is a real
    /// move, so the anchor is released.
    /// </summary>
    [Fact]
    public void OnGazeMoved_SustainedOutOfHoldGaze_ReleasesAnchor()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var anchorReleases = 0;
        _messenger.Register<ManualSelectionAnchorChangedMessage>(this, (r, m) =>
        {
            if (m.Anchor is null) anchorReleases++;
        });

        for (var i = 0; i < 11; i++)
            SendGaze(new PhysicalPoint(500, 500), i); // seats at sample 10
        for (var i = 11; i < 15; i++)
            SendGaze(new PhysicalPoint(700, 500), i); // 200 px off, 4 consecutive > skip limit 3

        Check.That(anchorReleases).IsEqualTo(1);

        _messenger.Unregister<ManualSelectionAnchorChangedMessage>(this);
    }

    /// <summary>
    /// Seating at the cluster centroid, not at the newest sample. The cluster is deliberately
    /// asymmetric so the centroid (500, 500.4) differs from the newest sample (500, 498).
    /// </summary>
    [Fact]
    public void OnGazeMoved_StabilityOnset_SeatsAnchorAtClusterCentroid()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        ManualSelectionAnchorChangedMessage? seated = null;
        _messenger.Register<ManualSelectionAnchorChangedMessage>(this, (r, m) =>
        {
            if (m.Anchor is not null) seated = m;
        });

        SendGazeRun(new PhysicalPoint(500, 500), FirstPostGraceSampleIndex);
        SendGaze(new PhysicalPoint(500, 500), 6);
        SendGaze(new PhysicalPoint(504, 500), 7);
        SendGaze(new PhysicalPoint(496, 500), 8);
        SendGaze(new PhysicalPoint(500, 504), 9);
        SendGaze(new PhysicalPoint(500, 498), 10);

        Check.That(seated).IsNotNull();
        Check.That(seated!.Anchor!.Value.X).IsCloseTo(500, 0.01);
        Check.That(seated!.Anchor!.Value.Y).IsCloseTo(500.4, 0.01);

        _messenger.Unregister<ManualSelectionAnchorChangedMessage>(this);
    }

    /// <summary>
    /// Hold tolerance is screen-space, so magnification must not appear in it — neither multiplied
    /// nor divided. 50 px is inside the 72 px hold and outside a divided 36 px one.
    /// </summary>
    [Fact]
    public void OnGazeMoved_HoldTolerance_IsNotDividedByMagnification()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var completed = false;
        var anchorReleases = 0;
        _messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) => completed = true);
        _messenger.Register<ManualSelectionAnchorChangedMessage>(this, (r, m) =>
        {
            if (m.Anchor is null) anchorReleases++;
        });

        for (var i = 0; i < 11; i++)
            SendGaze(new PhysicalPoint(500, 500), i); // seats at sample 10
        for (var i = 11; i < 40; i++)
            SendGaze(new PhysicalPoint(550, 500), i); // 50 px: inside 72, outside 72/2

        Check.WithCustomMessage("A 50 px offset is inside the 72 DIP hold and must keep the dwell alive.")
            .That(anchorReleases).IsEqualTo(0);
        Check.That(completed).IsTrue();

        _messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);
        _messenger.Unregister<ManualSelectionAnchorChangedMessage>(this);
    }

    /// <summary>
    /// The multiplied half of the same invariant: 100 px is outside the 72 px hold and inside a
    /// 72 x 2 = 144 px one.
    /// </summary>
    [Fact]
    public void OnGazeMoved_HoldTolerance_IsNotMultipliedByMagnification()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var anchorReleases = 0;
        _messenger.Register<ManualSelectionAnchorChangedMessage>(this, (r, m) =>
        {
            if (m.Anchor is null) anchorReleases++;
        });

        for (var i = 0; i < 11; i++)
            SendGaze(new PhysicalPoint(500, 500), i); // seats at sample 10
        for (var i = 11; i < 15; i++)
            SendGaze(new PhysicalPoint(600, 500), i); // 100 px: outside 72, inside 144

        Check.WithCustomMessage("A 100 px offset is outside the 72 DIP hold and must release the anchor.")
            .That(anchorReleases).IsEqualTo(1);

        _messenger.Unregister<ManualSelectionAnchorChangedMessage>(this);
    }

    /// <summary>
    /// The hold radius is a DIP value converted to physical pixels, so it carries ScaleX like the
    /// boundary does. At 150 % a 90 px offset is inside the 108 px hold; without ScaleX it would
    /// fall outside 72 and release the anchor.
    /// </summary>
    [Fact]
    public void OnGazeMoved_HoldTolerance_ScalesWithDisplayDpi()
    {
        DpiHelper.Initialize(1.5, 1.5);
        try
        {
            StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

            var anchorReleases = 0;
            _messenger.Register<ManualSelectionAnchorChangedMessage>(this, (r, m) =>
            {
                if (m.Anchor is null) anchorReleases++;
            });

            for (var i = 0; i < 11; i++)
                SendGaze(new PhysicalPoint(500, 500), i); // seats at sample 10
            for (var i = 11; i < 15; i++)
                SendGaze(new PhysicalPoint(590, 500), i); // 90 px: inside 72 x 1.5 = 108, outside 72

            Check.WithCustomMessage("At 150 % scaling the hold is 108 physical px; 90 px is inside it.")
                .That(anchorReleases).IsEqualTo(0);

            _messenger.Unregister<ManualSelectionAnchorChangedMessage>(this);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    /// <summary>
    /// The strictness knob: HoldRadiusLogical is read from settings, so raising it toward the
    /// visible rim loosens the hold without any code change. A hardcoded 72 px would release here.
    /// </summary>
    [Fact]
    public void OnGazeMoved_RaisedHoldToleranceSetting_LoosensTheHold()
    {
        _settings.ManualSelection.HoldRadiusLogical = 200;
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var anchorReleases = 0;
        _messenger.Register<ManualSelectionAnchorChangedMessage>(this, (r, m) =>
        {
            if (m.Anchor is null) anchorReleases++;
        });

        for (var i = 0; i < 11; i++)
            SendGaze(new PhysicalPoint(500, 500), i); // seats at sample 10
        for (var i = 11; i < 15; i++)
            SendGaze(new PhysicalPoint(650, 500), i); // 150 px: outside the default 72, inside 200

        Check.WithCustomMessage("HoldRadiusLogical must be read from settings, not hardcoded.")
            .That(anchorReleases).IsEqualTo(0);

        _messenger.Unregister<ManualSelectionAnchorChangedMessage>(this);
    }
}
