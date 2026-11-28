namespace EyeCtrl.App.Tests.Services;

using System.Reflection;
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
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Defines the App-layer test collection that isolates DpiHelper's process-global scale.
///
/// Membership rule: any class that CALLS DpiHelper.Initialize or DpiHelper.Update. xUnit v3 gives a
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
/// not contractual. That half is closed by the assembly-wide DpiHelperRestore hook (AssemblyInfo.cs,
/// declared here and in every writer-bearing project), not by each writer remembering a Dispose or
/// a finally — five tests in DisplayGeometryWatcherTests leaked 2.0 and the one that restored did
/// so outside a finally. Keep the hook and keep every writer in this collection; do not
/// reintroduce a per-class restore obligation.
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
    private readonly RecordingLogger _log = new();
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
            _log,
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

    /// <summary>
    /// The dwell-completion cue (spec D8): a completed ManualSelection dwell publishes
    /// <see cref="DwellActivationMessage"/> exactly once, in the same continuation as the
    /// completion message. The drive stops at the FIRST cue on purpose: the detector re-arms
    /// after an activation and MSDS runs it with cooldown disabled, so a held gaze re-completes
    /// roughly every dwell interval — the stream must not be allowed to reach a second one.
    /// </summary>
    [Fact]
    public void OnDwellCompleted_PublishesDwellActivation()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var completed = 0;
        var cueCount = 0;
        var completedAtCue = -1;
        _messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) => completed++);
        _messenger.Register<DwellActivationMessage>(this, (r, m) =>
        {
            cueCount++;
            completedAtCue = completed;
        });

        // Same stream shape as OnDwellCompleted_EmitsCompletionMessage: ~2.3 s covers the 300 ms
        // grace + 5-sample stability onset + 800 ms dwell with margin to spare. Stop at the FIRST
        // completion (not the first cue) so the setup leg below holds even before the publish
        // exists.
        for (var i = 0; i < 45 && completed == 0; i++)
            SendGaze(new PhysicalPoint(510, 510), i);

        Check.WithCustomMessage("Setup: the dwell must have completed for the cue to be meaningful.")
            .That(completed).IsEqualTo(1);
        Check.WithCustomMessage("A completed dwell must fire the cue exactly once.")
            .That(cueCount).IsEqualTo(1);
        Check.WithCustomMessage(
            "The completion message must be sent BEFORE the cue (spec D8 — a reorder inside " +
            "the continuation otherwise stays green).")
            .That(completedAtCue).IsEqualTo(1);

        _messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);
        _messenger.Unregister<DwellActivationMessage>(this);
    }

    /// <summary>
    /// The torn-down half of the race the continuation's bail-out guards: the detector raised
    /// TargetActivated, but the session ended before the DISPATCHED continuation ran
    /// (<c>OnManualSelectionEnded</c> nulls <c>_dwellDetector</c>). On the production dispatcher
    /// queue that ordering is the race itself; here it is forced deterministically — end the
    /// session (mirroring OnManualSelectionEnded_CleansUpResources), then invoke the handler the
    /// raise already called. On this thread DispatchSafe runs synchronously, so the continuation
    /// executes straight into the bail-out and must publish NEITHER the completion NOR the cue:
    /// the cue confirms an activation the user heard, and this activation never happened.
    /// </summary>
    [Fact]
    public void TornDownSession_PublishesNeitherCompletionNorCue()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var completed = 0;
        var cueCount = 0;
        _messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) => completed++);
        _messenger.Register<DwellActivationMessage>(this, (r, m) => cueCount++);

        // Teardown first — the session the raise belongs to is over.
        _messenger.Send(new EndManualSelectionMessage());

        // The raise arriving late: invoke its (now-detached) handler directly. The id is the
        // private ManualSelectionDwellTargetId const ("ManualSelection_dwell_target").
        typeof(ManualSelectionDwellService)
            .GetMethod("OnDwellActivated", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_service, new object[] { "ManualSelection_dwell_target" });

        Check.WithCustomMessage(
                "A continuation that runs after the session ended must not commit a click.")
            .That(completed).IsEqualTo(0);
        Check.WithCustomMessage(
                "A continuation that bails out must not fire the cue either: there was no " +
                "activation for the user to hear.")
            .That(cueCount).IsEqualTo(0);

        _messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);
        _messenger.Unregister<DwellActivationMessage>(this);
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

    /// <summary>
    /// F2 (magnifier-default-zoom fixup 01): the acceptance rim is the MESSAGE's radius times its
    /// magnification times ScaleX - 81 × 4 = 324 px here - never the raw setting. The m2c
    /// raw-setting mutant (200 × 4.0 = 800 px) would keep a 350-px gaze inside; the raw-pair
    /// variant (200 × 2.0 = 400 px, m2d) would too, so a gaze at 350 px must reset under the real
    /// expression.
    /// </summary>
    [Fact]
    public void OnGazeMoved_TheAcceptanceRimUsesTheMessagesEffectiveRadius()
    {
        _messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(500, 500), 81, 4.0, new PhysicalPoint(500, 500)));
        _interactionManager.OnFixationStarted(new PhysicalPoint(500, 500));
        _interactionManager.OnActivationButtonDwelled();

        var resets = 0;
        _messenger.Register<ResetManualSelectionIndicatorMessage>(this, (r, m) => resets++);

        SendGazeRun(new PhysicalPoint(850, 500), 14);   // 350 px: outside 324, inside the raw 400

        Check.WithCustomMessage(
                "A gaze outside the effective rim must reset, even inside the raw setting's rim.")
            .That(resets).IsStrictlyGreaterThan(0);

        _messenger.Unregister<ResetManualSelectionIndicatorMessage>(this);
    }

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
    /// M2 — commit at the frozen point. The anchor seats at (500, 500), then the held samples drift
    /// 40 px (inside the hold) until the dwell fires. The click must land on the ANCHOR, where the
    /// frozen ring is drawn, not on the running centroid of the held samples (which this stream
    /// drags ~30 px away) nor on the last sample. Replaces the earlier centroid-commit pin: the
    /// magnifier ring freeze (owner 18.11) deliberately moves the commit to the anchor.
    /// </summary>
    [Theory]
    [InlineData(60.0, 5, 10)]   // the owner's prep values (option B)
    [InlineData(72.0, 5, 3)]    // the product defaults
    public void Freeze_M2_CommitIsAtTheAnchor_NotTheCentroid(double hold, int k, int skip)
    {
        ConfigureHold(hold, k, skip);
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), k);
        for (var i = next; i < next + 60 && rec.Commits.Count == 0; i++)
            SendGaze(new PhysicalPoint(540, 500), i);

        Check.WithCustomMessage("Setup: the dwell must have completed.").That(rec.Commits).HasSize(1);
        Check.WithCustomMessage("The click must land on the anchor, where the frozen ring is drawn.")
            .That(rec.Commits[0].X).IsCloseTo(500, 0.01);
        Check.That(rec.Commits[0].Y).IsCloseTo(500, 0.01);
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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The magnifier ring freeze (owner 18.11, option B; ticket magnifier-ring-freeze, report §3).
    // The anchor seats on a tight settle (15 DIP, K of the last 7), the indicator is frozen on it
    // from the seat on, the click commits at it, and only the abandon, the rim, a recapture or the
    // session end unfreeze it. Each test names the report's mutant it kills; the D/K/skip theories
    // run at the owner's prep values (60 DIP, K 5, skip 10) and at the product defaults (72, 5, 3).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>M1 — the follow is gated: after the seat, gaze moving inside the hold radius sends
    /// no position message, and the anchor stays.</summary>
    [Theory]
    [InlineData(60.0, 5, 10)]
    [InlineData(72.0, 5, 3)]
    public void Freeze_M1_AfterTheSeat_GazeInsideTheHold_SendsNoPosition(double hold, int k, int skip)
    {
        ConfigureHold(hold, k, skip);
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), k);
        Check.WithCustomMessage("Setup: the anchor must have seated.").That(rec.Seats).HasSize(1);
        rec.Positions.Clear();

        // 8 samples (400 ms, under the 800 ms dwell) wandering up to 40 px — inside both holds.
        var wander = new[] { (540, 500), (500, 540), (460, 500), (500, 460), (530, 530), (470, 470), (520, 480), (480, 520) };
        for (var j = 0; j < wander.Length; j++)
            SendGaze(new PhysicalPoint(wander[j].Item1, wander[j].Item2), next + j);

        Check.WithCustomMessage("A frozen ring must not follow the gaze inside the hold.")
            .That(rec.Positions).IsEmpty();
        Check.That(rec.Releases).IsEqualTo(0);
        Check.That(rec.Commits).IsEmpty();
    }

    /// <summary>
    /// M3 — the onset is a fixed 15 DIP settle, not 0.5 × hold. With the owner's old 200 DIP hold
    /// the old rule seats on the 5th sample of a landing trajectory (every step 20 px, all within
    /// 100 px of the newest). The settle rule must wait until K samples of the last 7 lie within
    /// 15 DIP: the 5th sample at rest, 20 px past the last landing step.
    /// </summary>
    [Fact]
    public void Freeze_M3_ALandingTrajectory_DoesNotSeatUntilATightSettle()
    {
        ConfigureHold(200, 5, 10);
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);

        SendGazeRun(new PhysicalPoint(300, 500), FirstPostGraceSampleIndex);
        var i = FirstPostGraceSampleIndex;
        for (var step = 0; step < 8; step++, i++)
            SendGaze(new PhysicalPoint(300 + 20 * step, 500), i);   // 300 … 440, landing

        Check.WithCustomMessage("A landing gaze (20 px per sample) must not seat the anchor.")
            .That(rec.Seats).IsEmpty();

        for (var rest = 1; rest <= 5; rest++, i++)
        {
            SendGaze(new PhysicalPoint(460, 500), i);              // at rest, 20 px past the last step
            if (rest < 5)
                Check.WithCustomMessage($"Rest sample {rest} of 5 must not seat yet.").That(rec.Seats).IsEmpty();
        }

        Check.WithCustomMessage("The 5th sample at rest seats the anchor on the settled point.")
            .That(rec.Seats).HasSize(1);
        Check.That(rec.Seats[0].X).IsCloseTo(460, 0.01);
        Check.That(rec.Seats[0].Y).IsCloseTo(500, 0.01);
    }

    /// <summary>The onset radius is a DIP value and carries ScaleX: at 200 % a cluster with a 24 px
    /// spread seats (inside 30 px), and at 100 % it never could (outside 15 px).</summary>
    [Theory]
    [InlineData(2.0, true)]
    [InlineData(1.0, false)]
    public void Freeze_OnsetRadius_ScalesWithDisplayDpi(double scale, bool seats)
    {
        DpiHelper.Initialize(scale, scale);
        try
        {
            StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
            var rec = new FreezeRecorder(_messenger);

            for (var i = 0; i < FirstPostGraceSampleIndex + 10; i++)
                SendGaze(new PhysicalPoint(i % 2 == 0 ? 488 : 512, 500), i);

            Check.That(rec.Seats.Count == 1).IsEqualTo(seats);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    /// <summary>M4 — the abandon unfreezes: after more than the skip limit beyond D the ring follows
    /// the gaze again, on the releasing sample and on the next one.</summary>
    [Theory]
    [InlineData(60.0, 5, 10)]
    [InlineData(72.0, 5, 3)]
    public void Freeze_M4_AfterTheAbandon_TheIndicatorFollowsAgain(double hold, int k, int skip)
    {
        ConfigureHold(hold, k, skip);
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), k);
        rec.Positions.Clear();

        for (var j = 0; j <= skip; j++)
            SendGaze(new PhysicalPoint(700, 500), next + j);       // 200 px: beyond either D
        next += skip + 1;
        SendGaze(new PhysicalPoint(710, 500), next);

        Check.That(rec.Releases).IsEqualTo(1);
        Check.WithCustomMessage("After the abandon the next sample must move the indicator.")
            .That(rec.Positions).Not.IsEmpty();
        Check.That(rec.Positions[^1].X).IsCloseTo(710, 0.01);
        Check.That(rec.Positions[^1].Y).IsCloseTo(500, 0.01);
    }

    /// <summary>M8 — <c>&gt;</c>, not <c>&gt;=</c>: exactly N (the skip limit) out-of-hold samples keep
    /// the freeze; the N+1-th releases it and puts the ring on the live gaze.</summary>
    [Theory]
    [InlineData(60.0, 5, 10)]
    [InlineData(72.0, 5, 3)]
    public void Freeze_M8_ExactlyTheSkipLimitKeepsTheFreeze_OneMoreReleases(double hold, int k, int skip)
    {
        ConfigureHold(hold, k, skip);
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), k);
        rec.Positions.Clear();

        for (var j = 0; j < skip; j++)
            SendGaze(new PhysicalPoint(700, 500), next + j);

        Check.WithCustomMessage("Exactly the skip limit of out-of-hold samples must keep the freeze.")
            .That(rec.Positions).IsEmpty();
        Check.That(rec.Releases).IsEqualTo(0);

        SendGaze(new PhysicalPoint(700, 500), next + skip);

        Check.WithCustomMessage("One more out-of-hold sample must release the freeze.")
            .That(rec.Releases).IsEqualTo(1);
        Check.That(rec.Positions).HasSize(1);
        Check.That(rec.Positions[0].X).IsCloseTo(700, 0.01);
    }

    /// <summary>M5 — leaving the magnifier rim unfreezes, at once and for the samples after it.</summary>
    [Theory]
    [InlineData(60.0, 5, 10)]
    [InlineData(72.0, 5, 3)]
    public void Freeze_M5_LeavingTheRim_Unfreezes(double hold, int k, int skip)
    {
        ConfigureHold(hold, k, skip);
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), k);
        rec.Positions.Clear();

        SendGaze(new PhysicalPoint(950, 500), next);               // 450 px: outside the 400 px rim
        SendGaze(new PhysicalPoint(520, 500), next + 1);           // back inside, 20 px off the old anchor

        Check.That(rec.Releases).IsEqualTo(1);
        Check.WithCustomMessage("Leaving the rim must unfreeze the ring onto the live gaze.")
            .That(rec.Positions).HasSize(2);
        Check.That(rec.Positions[0].X).IsCloseTo(950, 0.01);
        Check.That(rec.Positions[1].X).IsCloseTo(520, 0.01);
    }

    /// <summary>M5 — a recapture unfreezes: the next sample (in the restarted grace) moves the ring.</summary>
    [Theory]
    [InlineData(60.0, 5, 10)]
    [InlineData(72.0, 5, 3)]
    public void Freeze_M5_ARecapture_Unfreezes(double hold, int k, int skip)
    {
        ConfigureHold(hold, k, skip);
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), k);
        rec.Positions.Clear();

        _messenger.Send(new ManualSelectionRecaptureMessage());
        SendGaze(new PhysicalPoint(530, 500), next);               // inside the hold: only the recapture can free it

        Check.That(rec.Releases).IsEqualTo(1);
        Check.WithCustomMessage("After a recapture the ring must follow the gaze again.")
            .That(rec.Positions).HasSize(1);
        Check.That(rec.Positions[0].X).IsCloseTo(530, 0.01);
    }

    /// <summary>The session end unfreezes: a new session's first sample moves the ring.</summary>
    [Fact]
    public void Freeze_TheSessionEnd_Unfreezes()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);
        var next = SeatAnchorAt(new PhysicalPoint(500, 500), 5);

        _messenger.Send(new EndManualSelectionMessage());
        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 500), 200, 2.0, new PhysicalPoint(500, 500)));
        rec.Positions.Clear();
        SendGaze(new PhysicalPoint(510, 500), next);

        Check.That(rec.Positions).HasSize(1);
        Check.That(rec.Positions[0].X).IsCloseTo(510, 0.01);
    }

    /// <summary>
    /// M6 — the seat sends ONE final position, the anchor. The cluster is asymmetric, so the anchor
    /// (500, 500.4) is not the seating sample (500, 498): a seat that leaves the ring on the last
    /// live sample fails the equality.
    /// </summary>
    [Fact]
    public void Freeze_M6_TheLastPositionAtTheSeat_IsTheAnchor()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);

        SendGazeRun(new PhysicalPoint(500, 500), FirstPostGraceSampleIndex);
        SendGaze(new PhysicalPoint(500, 500), 6);
        SendGaze(new PhysicalPoint(504, 500), 7);
        SendGaze(new PhysicalPoint(496, 500), 8);
        SendGaze(new PhysicalPoint(500, 504), 9);
        SendGaze(new PhysicalPoint(500, 498), 10);

        Check.That(rec.Seats).HasSize(1);
        var last = rec.Positions[^1];
        Check.WithCustomMessage("The ring's last position at the seat must be the anchor.")
            .That(last.X).IsCloseTo(rec.Seats[0].X, 0.001);
        Check.That(last.Y).IsCloseTo(rec.Seats[0].Y, 0.001);
        Check.That(last.Y).IsCloseTo(500.4, 0.001);
    }

    /// <summary>M7 — a blink keeps the freeze: gap samples during it send nothing (even far off the
    /// anchor), and the next real sample inside the hold sends nothing either.</summary>
    [Theory]
    [InlineData(60.0, 5, 10)]
    [InlineData(72.0, 5, 3)]
    public void Freeze_M7_GapSamplesDuringTheFreeze_SendNothing(double hold, int k, int skip)
    {
        ConfigureHold(hold, k, skip);
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), k);
        rec.Positions.Clear();

        for (var j = 0; j < skip + 3; j++)
            _messenger.Send(new GazeCursorMovedMessage(
                new GazeFrame(new PhysicalPoint(700, 500), (next + j) * TargetingSampleSpacingUs, IsInGap: true)));
        SendGaze(new PhysicalPoint(510, 500), next + skip + 3);

        Check.WithCustomMessage("A blink must not move or unfreeze the frozen ring.")
            .That(rec.Positions).IsEmpty();
        Check.That(rec.Releases).IsEqualTo(0);
    }

    // ── The gated log lines (diagnostics.logRingEvents) ──────────────────────────────────────

    /// <summary>The seat, a silence restart and the commit, with their fields, when the key is on.</summary>
    [Fact]
    public void Freeze_LogLines_SeatSilenceAndCommit_AreWrittenWhenRingEventsAreOn()
    {
        _settings.Diagnostics.LogRingEvents = true;
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), 5);    // seats at sample 10 = 500 ms
        SendGaze(new PhysicalPoint(540, 500), next);                // 11
        next += 5;                                                  // 250 ms silence: 11 → 16
        for (var i = next; i < next + 40 && rec.Commits.Count == 0; i++)
            SendGaze(new PhysicalPoint(540, 500), i);

        Check.That(rec.Commits).HasSize(1);
        var lines = _log.MagnifierLines();
        Check.That(lines).Contains("Magnifier dwell seat ts=500000 ms=500 anchor=(500,500) k=5/7");
        Check.That(lines).Contains("Magnifier dwell silence ts=800000 gapMs=250 anchor=(500,500)");
        Check.That(lines.Any(l => l.StartsWith("Magnifier dwell commit ts=", StringComparison.Ordinal)
                                  && l.Contains("anchor=(500,500) centroid=(", StringComparison.Ordinal)))
            .IsTrue();
    }

    /// <summary>Every reset with its cause: hold (the abandon), bounds once per excursion outside the
    /// rim, and recapture with the anchor it dropped.</summary>
    [Fact]
    public void Freeze_LogLines_ResetsCarryTheirCause()
    {
        _settings.Diagnostics.LogRingEvents = true;
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), 5);    // default skip limit 3
        for (var j = 0; j < 4; j++)
            SendGaze(new PhysicalPoint(700, 500), next + j);        // abandon on the 4th
        next += 4;
        SendGaze(new PhysicalPoint(950, 500), next++);              // out of the rim …
        SendGaze(new PhysicalPoint(960, 500), next++);              // … still out: no second line
        for (var j = 0; j < 5; j++)
            SendGaze(new PhysicalPoint(520, 500), next++);          // back in, re-seats
        _messenger.Send(new ManualSelectionRecaptureMessage());

        var lines = _log.MagnifierLines();
        Check.That(lines).Contains("Magnifier dwell reset (hold) ts=700000 anchor=(500,500)");
        Check.That(lines.Count(l => l.StartsWith("Magnifier dwell reset (bounds)", StringComparison.Ordinal)))
            .IsEqualTo(1);
        Check.That(lines).Contains("Magnifier dwell reset (bounds) ts=750000 anchor=none");
        Check.That(lines).Contains("Magnifier dwell reset (recapture) ts=na anchor=(520,500)");
    }

    /// <summary>
    /// Review-01 F3: a recapture while the gaze is outside the rim ends that excursion. The gaze
    /// comes back inside during the new grace (those samples return before the bounds check) and
    /// leaves again after it: that new excursion must still write its bounds line.
    /// </summary>
    [Fact]
    public void Freeze_LogLines_ARecaptureWhileOutside_StillLogsTheNextExcursion()
    {
        _settings.Diagnostics.LogRingEvents = true;
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), 5);    // seats at sample 10
        SendGaze(new PhysicalPoint(950, 500), next++);              // 11: out, logged
        SendGaze(new PhysicalPoint(950, 500), next++);              // 12: still out
        _messenger.Send(new ManualSelectionRecaptureMessage());
        for (var j = 0; j < 6; j++)
            SendGaze(new PhysicalPoint(500, 500), next++);          // 13–18: inside, all in the new 300 ms grace
        SendGaze(new PhysicalPoint(950, 500), next++);              // 19: out again, after the grace

        var bounds = _log.MagnifierLines()
            .Where(l => l.StartsWith("Magnifier dwell reset (bounds)", StringComparison.Ordinal))
            .ToList();
        Check.That(bounds).ContainsExactly(
            "Magnifier dwell reset (bounds) ts=550000 anchor=(500,500)",
            "Magnifier dwell reset (bounds) ts=950000 anchor=none");
    }

    /// <summary>The gate: with diagnostics.logRingEvents off (the default) none of the lines is written.</summary>
    [Fact]
    public void Freeze_LogLines_AreGatedOnRingEvents()
    {
        Check.That(_settings.Diagnostics.LogRingEvents).IsFalse();
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var rec = new FreezeRecorder(_messenger);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), 5);
        next += 5;                                                  // a silence
        for (var i = next; i < next + 40 && rec.Commits.Count == 0; i++)
            SendGaze(new PhysicalPoint(540, 500), i);
        for (var j = 0; j < 4; j++)
            SendGaze(new PhysicalPoint(700, 500), next + 40 + j);
        SendGaze(new PhysicalPoint(950, 500), next + 44);
        _messenger.Send(new ManualSelectionRecaptureMessage());

        Check.That(rec.Commits).HasSize(1);
        Check.WithCustomMessage("Without diagnostics.logRingEvents no magnifier dwell line may be written.")
            .That(_log.MagnifierLines()).IsEmpty();
    }

    /// <summary>The commit's real-point line (written in OverlayMessageCoordinator): invariant, whole px.</summary>
    [Fact]
    public void Freeze_CommitRealLine_HasTheExactFormat()
    {
        Check.That(ManualSelectionDwellService.CommitRealLine(new PhysicalPoint(1000.4, 600.6), new PhysicalPoint(1234.4, 987.2)))
            .IsEqualTo("Magnifier dwell commit real=(1234,987) view=(1000,601)");
    }

    // ── The no-seat auto-close (ManualSelectionSettings.NoSeatCloseMs) ───────────────────────
    //
    // Grace is 300 ms and samples are 50 ms apart, so index 6 (ts 300 ms) is the first post-grace
    // sample and index 26 (ts 1300 ms) is exactly 1000 ms after it. The rim is 200 x 2.0 = 400 px
    // physical, so (1000,500) is outside it and (500,500) is the centre.

    /// <summary>T1: no seat, out of the rim for exactly the threshold after the grace → one close,
    /// attributed to no-seat. One sample before the threshold: no close.</summary>
    [Fact]
    public void NoSeat_OutOfRimForTheThreshold_ClosesExactlyOnceWithTheNoSeatReason()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var close = new CloseRecorder(_messenger);

        for (var i = 0; i < 26; i++)
            SendGaze(new PhysicalPoint(1000, 500), i);      // 0–1250 ms: the post-grace run is 950 ms
        Check.WithCustomMessage("One sample before the threshold no close may be sent.")
            .That(close.Count).IsEqualTo(0);

        SendGaze(new PhysicalPoint(1000, 500), 26);         // 1300 ms: exactly 1000 ms after the grace
        Check.WithCustomMessage("A no-seat run that reaches the threshold must close the magnifier.")
            .That(close.Count).IsEqualTo(1);
        Check.WithCustomMessage("The close must be attributed to the no-seat reason.")
            .That(close.Reasons[0]).IsEqualTo(MagnifierCloseReasons.NoSeat);
    }

    /// <summary>T2: an excursion shorter than the threshold, back INSIDE the rim, then out again —
    /// the timer restarted, so the second run needs its own full threshold. Resuming inside (rather
    /// than merely outside the rim) is what makes a cleared timer necessary.</summary>
    [Fact]
    public void NoSeat_BackInsideTheRim_RestartsTheTimer()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var close = new CloseRecorder(_messenger);

        for (var i = 0; i < 23; i++)
            SendGaze(new PhysicalPoint(1000, 500), i);      // 1100 ms: an 800 ms run
        Check.That(close.Count).IsEqualTo(0);

        SendGaze(new PhysicalPoint(500, 500), 23);          // back inside: the run ends here
        SendGaze(new PhysicalPoint(500, 500), 24);

        for (var i = 25; i < 45; i++)
            SendGaze(new PhysicalPoint(1000, 500), i);      // second run from 1250 ms: 950 ms
        Check.WithCustomMessage("The second excursion must be measured from its own first sample.")
            .That(close.Count).IsEqualTo(0);

        SendGaze(new PhysicalPoint(1000, 500), 45);         // 2250 ms: the second run reaches 1000 ms
        Check.That(close.Count).IsEqualTo(1);
    }

    /// <summary>T3 (MG2b): seated, then released by hold, then out of the rim for five thresholds —
    /// never closes. Dropping the session flag would fire the timer on this excursion.</summary>
    [Fact]
    public void SeatedThenReleasedByHold_OutOfRimForFiveThresholds_NeverCloses()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var close = new CloseRecorder(_messenger);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), 5);   // seats at index 10
        for (var j = 0; j < 4; j++)
            SendGaze(new PhysicalPoint(700, 500), next + j);        // out of the 72 px hold, inside the rim
        next += 4;

        for (var j = 0; j < 100; j++)
            SendGaze(new PhysicalPoint(1000, 500), next + j);       // 5 s continuously out of the rim
        Check.WithCustomMessage("MG2b: a session that has seated is never auto-closed, even after a release.")
            .That(close.Count).IsEqualTo(0);
    }

    /// <summary>T4 (MG1): seated and frozen, gaze out of the rim — the existing bounds reset still
    /// happens and no close is sent, however long the gaze stays out.</summary>
    [Fact]
    public void SeatedAndFrozen_OutOfRim_StillResetsAndNeverCloses()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var close = new CloseRecorder(_messenger);
        var resets = 0;
        _messenger.Register<ResetManualSelectionIndicatorMessage>(this, (r, m) => resets++);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), 5);
        for (var j = 0; j < 100; j++)
            SendGaze(new PhysicalPoint(1000, 500), next + j);

        Check.WithCustomMessage("MG1: the bounds reset must still run for a seated, frozen ring.")
            .That(resets).IsStrictlyGreaterThan(0);
        Check.That(close.Count).IsEqualTo(0);

        _messenger.Unregister<ResetManualSelectionIndicatorMessage>(this);
    }

    /// <summary>T5: out of the rim only during the grace, then still out at grace end — the timer
    /// starts at the first post-grace sample, not at the session start. A timer started during the
    /// grace would have fired at 1000 ms from the session start (index 20).</summary>
    [Fact]
    public void OutOfRimDuringTheGrace_TimerStartsAtTheFirstPostGraceSample()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var close = new CloseRecorder(_messenger);

        for (var i = 0; i <= 20; i++)
            SendGaze(new PhysicalPoint(1000, 500), i);      // reaches 1000 ms, all out of the rim
        Check.WithCustomMessage(
                "The grace's out-of-rim samples must not start the timer: at 1000 ms from the session "
                + "start the post-grace run is only 700 ms old.")
            .That(close.Count).IsEqualTo(0);

        SendGaze(new PhysicalPoint(1000, 500), 26);         // 1300 ms: 1000 ms after the first post-grace sample
        Check.That(close.Count).IsEqualTo(1);
    }

    /// <summary>T6: 0 turns the close off.</summary>
    [Fact]
    public void NoSeatCloseMsZero_NeverCloses()
    {
        _settings.ManualSelection.NoSeatCloseMs = 0;
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var close = new CloseRecorder(_messenger);

        for (var i = 0; i < 100; i++)
            SendGaze(new PhysicalPoint(1000, 500), i);      // 5 s out of the rim

        Check.That(close.Count).IsEqualTo(0);
    }

    /// <summary>T7: a recapture is not a new session — the seat flag survives it, so the excursion
    /// after the recapture never closes.</summary>
    [Fact]
    public void RecaptureAfterSeat_KeepsTheSeatFlag_NoClose()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var close = new CloseRecorder(_messenger);

        var next = SeatAnchorAt(new PhysicalPoint(500, 500), 5);
        _messenger.Send(new ManualSelectionRecaptureMessage());

        for (var j = 0; j < 100; j++)
            SendGaze(new PhysicalPoint(1000, 500), next + j);

        Check.WithCustomMessage("A recapture is not a new session: the seat flag must survive it.")
            .That(close.Count).IsEqualTo(0);
    }

    /// <summary>T9: the close is sent once; out-of-rim samples that keep arriving before the
    /// session ends must not re-send it.</summary>
    [Fact]
    public void NoSeat_MoreOutOfRimSamplesAfterTheClose_StillOneClose()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var close = new CloseRecorder(_messenger);

        for (var i = 0; i < 60; i++)
            SendGaze(new PhysicalPoint(1000, 500), i);      // 3 s out, well past the threshold

        Check.That(close.Count).IsEqualTo(1);
    }

    /// <summary>The gated diagnostic line: written with the deciding sample's device timestamp and
    /// the measured run length.</summary>
    [Fact]
    public void NoSeat_LogsTheGatedDiagnosticLine()
    {
        _settings.Diagnostics.LogRingEvents = true;
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));

        for (var i = 0; i < 27; i++)
            SendGaze(new PhysicalPoint(1000, 500), i);      // index 26 decides the close

        Check.That(_log.MagnifierLines())
            .Contains("Magnifier dwell no-seat close ts=1300000 outMs=1000");
    }

    /// <summary>T11 (the #51 shape): a hover just past the rim but inside rim + 40 DIP, held for
    /// three thresholds, never closes — the dwell still resets at the plain rim, and the extra
    /// margin is what keeps the user "still aiming" from counting as leaving.</summary>
    [Fact]
    public void JustPastTheRimButInsideTheMargin_ForThreeThresholds_NeverCloses()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var close = new CloseRecorder(_messenger);
        var resets = 0;
        _messenger.Register<ResetManualSelectionIndicatorMessage>(this, (r, m) => resets++);

        // 410 px from the centre: outside the 400 px rim, inside 400 + 40 = 440 px.
        for (var i = 0; i < 27; i++)
            SendGaze(new PhysicalPoint(910, 500), i);       // 1.3 s, the #51 session's hover
        Check.WithCustomMessage("The #51 hover just past the rim must not close at 1.3 s.")
            .That(close.Count).IsEqualTo(0);

        for (var i = 27; i < 70; i++)
            SendGaze(new PhysicalPoint(910, 500), i);       // 3.5 s, three times the threshold
        Check.WithCustomMessage(
                "A hover just past the rim, inside the margin, is the user still aiming: no close.")
            .That(close.Count).IsEqualTo(0);
        Check.WithCustomMessage("The plain-rim bounds reset must still run inside the margin.")
            .That(resets).IsStrictlyGreaterThan(0);

        _messenger.Unregister<ResetManualSelectionIndicatorMessage>(this);
    }

    /// <summary>T12: one sample beyond the margin starts the timer, then one sample between the rim
    /// and the margin clears it — the run after it needs its own full threshold.</summary>
    [Fact]
    public void BeyondTheMarginThenBackInsideIt_ClearsTheTimer()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var close = new CloseRecorder(_messenger);

        for (var i = 0; i <= 21; i++)
            SendGaze(new PhysicalPoint(1000, 500), i);      // beyond 440 px; post-grace run 750 ms

        SendGaze(new PhysicalPoint(910, 500), 22);          // 410 px: past the rim, inside the margin

        for (var i = 23; i < 43; i++)
            SendGaze(new PhysicalPoint(1000, 500), i);      // second run from 1150 ms: 950 ms
        Check.WithCustomMessage("The sample inside the margin must clear the timer.")
            .That(close.Count).IsEqualTo(0);

        SendGaze(new PhysicalPoint(1000, 500), 43);         // 2150 ms: the second run reaches 1000 ms
        Check.That(close.Count).IsEqualTo(1);
    }

    /// <summary>The margin is screen space like the hold radius: at 150 % scaling the rim is 600 px
    /// and the 40-DIP margin is 60 px, so 650 px is inside the outer radius. Dropping ScaleX from
    /// the margin (40 px → 640) would close this wanted session.</summary>
    [Fact]
    public void Margin_ScalesWithDisplayDpi()
    {
        DpiHelper.Initialize(1.5, 1.5);
        try
        {
            StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
            var close = new CloseRecorder(_messenger);

            for (var i = 0; i < 70; i++)
                SendGaze(new PhysicalPoint(1150, 500), i);  // 650 px: past the 600 px rim, inside 660

            Check.That(close.Count).IsEqualTo(0);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    /// <summary>The margin is NOT magnified: the visible rim here is 200 x 2.0 = 400 px and the
    /// margin stays 40 px, so the outer radius is 440 and a 460 px sample is gone. A margin scaled
    /// by the magnification (2 x 40 = 80 px → 480) would keep it inside and never close.</summary>
    [Fact]
    public void Margin_IsNotMultipliedByMagnification()
    {
        StartSession(new PhysicalPoint(500, 500), new PhysicalPoint(500, 500));
        var close = new CloseRecorder(_messenger);

        for (var i = 0; i < 70; i++)
            SendGaze(new PhysicalPoint(960, 500), i);       // 460 px: beyond 440

        Check.That(close.Count).IsEqualTo(1);
        Check.That(close.Reasons[0]).IsEqualTo(MagnifierCloseReasons.NoSeat);
    }

    // ── Freeze helpers ───────────────────────────────────────────────────────────────────────

    private void ConfigureHold(double hold, int k, int skip)
    {
        _settings.ManualSelection.HoldRadiusLogical = hold;
        _settings.ManualSelection.StabilitySampleCount = k;
        _settings.ManualSelection.HoldOutlierSkipLimit = skip;
    }

    /// <summary>Grace samples, then K samples at <paramref name="point"/> (the K-th seats); returns
    /// the next sample index.</summary>
    private int SeatAnchorAt(PhysicalPoint point, int k)
    {
        for (var i = 0; i < FirstPostGraceSampleIndex + k; i++)
            SendGaze(point, i);
        return FirstPostGraceSampleIndex + k;
    }

    /// <summary>Records what the freeze is observed by: the indicator positions, the seats and
    /// releases of the anchor, and the commit points.</summary>
    private sealed class FreezeRecorder
    {
        public List<PhysicalPoint> Positions { get; } = new();
        public List<PhysicalPoint> Seats { get; } = new();
        public int Releases { get; private set; }
        public List<PhysicalPoint> Commits { get; } = new();

        public FreezeRecorder(IMessenger messenger)
        {
            messenger.Register<FreezeRecorder, UpdateManualSelectionIndicatorPositionMessage>(this, (r, m) => r.Positions.Add(m.GazePoint));
            messenger.Register<FreezeRecorder, ManualSelectionAnchorChangedMessage>(this, (r, m) =>
            {
                if (m.Anchor is { } anchor) r.Seats.Add(anchor);
                else r.Releases++;
            });
            messenger.Register<FreezeRecorder, ManualSelectionDwellCompletedMessage>(this, (r, m) => r.Commits.Add(m.PrecisePoint));
        }
    }

    /// <summary>Records every close the service sends, with the reason the sender named.</summary>
    private sealed class CloseRecorder
    {
        public List<string?> Reasons { get; } = new();

        public int Count => Reasons.Count;

        public CloseRecorder(IMessenger messenger) =>
            messenger.Register<CloseRecorder, ManualSelectionCloseMessage>(
                this, (r, m) => r.Reasons.Add(m.CloseReason));
    }

    private sealed class RecordingLogger : ILogger<ManualSelectionDwellService>
    {
        public List<string> Lines { get; } = new();

        public List<string> MagnifierLines() =>
            Lines.Where(l => l.StartsWith(ManualSelectionDwellService.LogPrefix, StringComparison.Ordinal)).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Information)
                Lines.Add(formatter(state, exception));
        }
    }
}
