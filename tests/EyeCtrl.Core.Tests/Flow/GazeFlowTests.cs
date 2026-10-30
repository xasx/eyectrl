namespace EyeCtrl.Core.Tests.Flow;

using System.Linq;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// End-to-end interaction-flow tests driven entirely by synthetic gaze through the real
/// GazeEngine + InteractionManager. Fully headless: no windows, no gaze hardware, and no real
/// cursor/input (the harness records input instead), so these are safe to run without disturbing
/// an eye-tracking user and complete in milliseconds.
///
/// The gaze→fixation edge is exercised for real (through <see cref="EyeCtrl.Core.Processing.FixationDetector"/>);
/// the activation-button and menu-button dwells live in the ViewModel layer, so they are simulated
/// here via the InteractionManager callbacks the ViewModels would fire.
/// </summary>
public class GazeFlowTests
{
    [Fact]
    public void HoldingGaze_TriggersFixation_AndFreezesCursor()
    {
        var h = new GazeFlowHarness();

        h.Hold(500, 400, durationMs: 450); // longer than FixationDetectionMs (300ms default)

        Check.That(h.State).IsEqualTo(InteractionState.Fixation);
        Check.That(h.States[0]).IsEqualTo(InteractionState.Fixation); // FreeLook → Fixation was the first transition
        Check.That(h.Onsets.Count).IsEqualTo(1);
        Check.That(h.Onsets[0].X).IsEqualTo(500d);
        Check.That(h.Onsets[0].Y).IsEqualTo(400d);

        // The cursor moved while free-looking, then must FREEZE once fixating — the core behavior
        // that keeps an eye-tracking user from losing their pointer while aiming.
        var movesWhenFixated = h.Input.MoveCount;
        Check.That(movesWhenFixated).IsStrictlyGreaterThan(0);

        h.Hold(500, 400, durationMs: 300); // keep looking at the same spot
        Check.That(h.Input.MoveCount).IsEqualTo(movesWhenFixated); // no further cursor movement
    }

    [Fact]
    public void ActionMenuMode_FixationToMenuToClick()
    {
        var h = new GazeFlowHarness(SelectionMode.None);

        h.Hold(600, 500, durationMs: 450);
        Check.That(h.State).IsEqualTo(InteractionState.Fixation);

        h.Interaction.OnActivationButtonDwelled(); // ViewModel dwell would fire this
        Check.That(h.State).IsEqualTo(InteractionState.ActionMenuVisible);
        Check.That(h.ActionMenusShown.Count).IsEqualTo(1);
        Check.That(h.ActionMenusShown[0].X).IsEqualTo(600d);

        h.Interaction.OnActionSelected(InteractionAction.LeftClick); // picking a menu button
        Check.That(h.ActionsRequested.Select(a => a.Action)).Contains(InteractionAction.LeftClick);
    }

    [Fact]
    public void ManualSelectionMode_FixationToManualSelectionToActionMenu()
    {
        var h = new GazeFlowHarness(SelectionMode.ManualSelection);

        h.Hold(700, 300, durationMs: 450);
        Check.That(h.State).IsEqualTo(InteractionState.Fixation);

        h.Interaction.OnActivationButtonDwelled();
        Check.That(h.State).IsEqualTo(InteractionState.ManualSelectionActive);
        Check.That(h.ManualSelectionsStarted.Count).IsEqualTo(1);

        var precise = new PhysicalPoint(720, 315);
        h.Interaction.OnManualSelectionDwell(precise); // precise dwell inside the magnifier
        Check.That(h.State).IsEqualTo(InteractionState.ManualSelectionActionMenuVisible);
        Check.That(h.ActionMenusShown.Count).IsEqualTo(1);
        Check.That(h.ActionMenusShown[0].X).IsEqualTo(720d); // menu opens at the precise point
    }

    [Fact]
    public void LosingFixation_ReturnsToFreeLook()
    {
        var h = new GazeFlowHarness();

        h.Hold(500, 400, durationMs: 450);
        Check.That(h.State).IsEqualTo(InteractionState.Fixation);

        h.Hold(1400, 1000, durationMs: 100); // gaze jumps far away, briefly (not long enough to re-fixate)
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void SelectingScrollMode_EntersScrollMode_ThenExitReturnsToFreeLook()
    {
        var h = new GazeFlowHarness(SelectionMode.None);

        h.FixateAndActivate(600, 500);
        h.Interaction.OnActionSelected(InteractionAction.ScrollMode);

        Check.That(h.State).IsEqualTo(InteractionState.ScrollMode);
        Check.That(h.Interaction.ScrollAnchor.HasValue).IsTrue();

        h.Interaction.ExitScrollMode();
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
        Check.That(h.Interaction.ScrollAnchor.HasValue).IsFalse();
    }

    [Fact]
    public void SelectingDrag_EntersDragging_ThenCompleteReturnsToFreeLook()
    {
        var h = new GazeFlowHarness(SelectionMode.None);

        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.Drag);

        Check.That(h.State).IsEqualTo(InteractionState.Dragging);
        Check.That(h.DragsStarted.Count).IsEqualTo(1); // grab origin handed to the drag overlay

        h.Interaction.CompleteDrag();
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void Dragging_CursorFollowsGaze()
    {
        // A drag is MouseDown at the grab point, the pointer carried by the gaze, MouseUp at the drop:
        // without the moves the dragged thing never travels. Pinned by re-check 4 of the pointer hold
        // (b546dc7f), which rewrote this rule. Mutation: drop Dragging from the pointer rule in
        // GazeEngine.OnGazePoint → no move, red.
        var h = new GazeFlowHarness(SelectionMode.None);

        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.Drag);
        Check.That(h.State).IsEqualTo(InteractionState.Dragging);

        var movesBefore = h.Input.MoveCount;
        h.Gaze(900, 700);
        Check.That(h.Input.MoveCount).IsStrictlyGreaterThan(movesBefore);
    }

    [Fact]
    public void RepeatMode_CursorKeepsFollowingGaze()
    {
        var h = new GazeFlowHarness(SelectionMode.None);

        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.LeftClick);  // back to FreeLook
        h.Interaction.EnterRepeatMode(InteractionAction.LeftClick);
        Check.That(h.State).IsEqualTo(InteractionState.RepeatMode);

        // Unlike fixating (which freezes the pointer while aiming), repeat mode keeps the cursor
        // glued to gaze — the user aims the repeating click BY looking, and must see where it lands.
        var movesBefore = h.Input.MoveCount;
        h.Gaze(900, 700);
        Check.That(h.Input.MoveCount).IsStrictlyGreaterThan(movesBefore);
    }

    [Fact]
    public void RepeatMode_GazeLost_AutoExitsAfterTwoSeconds()
    {
        // Review PR #139 finding 1 core: every filtered view of lost eyes looks valid, so the
        // exit reads RAW validity (NoteGazeSample). The user cannot dwell the stop button or the
        // menu while their eyes are away — the mode must end itself.
        var h = new GazeFlowHarness(SelectionMode.None);
        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.LeftClick);
        h.Interaction.EnterRepeatMode(InteractionAction.LeftClick);

        h.HoldInvalid(640, 480, durationMs: 2500);
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void RepeatMode_HeldGaze_FiresTheLockedActionAtTheFixatedPoint()
    {
        // End-to-end dwell-to-act (D23-D25): fixation arms the dwell, valid samples advance it,
        // and the locked action fires AT the fixated point after RepeatDwellMs.
        var h = new GazeFlowHarness(SelectionMode.None);
        h.Interaction.RepeatDwellMs = 400;
        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.LeftClick);  // back to FreeLook
        h.Interaction.EnterRepeatMode(InteractionAction.RightClick);
        var baseline = h.ActionsRequested.Count;

        h.Hold(800, 600, durationMs: 1100);   // 300 fixation + 400 dwell + margin

        Check.That(h.State).IsEqualTo(InteractionState.RepeatMode);   // stays locked
        var fired = h.ActionsRequested.Skip(baseline).ToList();
        Check.That(fired).HasSize(1);
        Check.That(fired[0].Action).IsEqualTo(InteractionAction.RightClick);
        Check.That(fired[0].Point.X).IsCloseTo(800, 60);
        Check.That(fired[0].Point.Y).IsCloseTo(600, 60);
    }

    [Fact]
    public void RepeatMode_HeldOnScreen_WithBoundsPublished_StillFires()
    {
        // C4 control: the new off-screen gate must not refuse a legitimate on-screen run. The
        // dwell fires at the same point and time as before the gate existed.
        var h = new GazeFlowHarness(SelectionMode.None);
        h.Interaction.RepeatDwellMs = 400;
        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.LeftClick);  // back to FreeLook
        h.Interaction.EnterRepeatMode(InteractionAction.RightClick);
        h.Interaction.SetRepeatScreenBounds(new PhysicalRect(0, 0, 1920, 1080), tolerancePhysical: 10);
        var baseline = h.ActionsRequested.Count;

        h.Hold(800, 600, durationMs: 1100);

        Check.That(h.State).IsEqualTo(InteractionState.RepeatMode);
        var fired = h.ActionsRequested.Skip(baseline).ToList();
        Check.That(fired).HasSize(1);
        Check.That(fired[0].Action).IsEqualTo(InteractionAction.RightClick);
        Check.That(fired[0].Point.X).IsCloseTo(800, 60);
        Check.That(fired[0].Point.Y).IsCloseTo(600, 60);
    }

    [Theory]
    [InlineData(960, 1080, 960, 1120)]   // 40 px past the bottom edge
    [InlineData(0, 540, -40, 540)]       // 40 px past the left edge
    [InlineData(1920, 540, 1960, 540)]   // 40 px past the right edge
    public void RepeatMode_GlidePastABandLessEdge_DoesNotFire(
        double armX, double armY, double glideX, double glideY)
    {
        // C4 end-to-end through the real fixation detector: the ~40 px glide leaves the fixation
        // alive (radius 75 px), so a click at the stale on-screen arm point would fire. The dwell
        // must instead be disarmed by the sample's own filtered position.
        var h = new GazeFlowHarness(SelectionMode.None);
        h.Interaction.RepeatDwellMs = 400;
        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.LeftClick);  // back to FreeLook
        h.Interaction.EnterRepeatMode(InteractionAction.RightClick);
        h.Interaction.SetRepeatScreenBounds(new PhysicalRect(0, 0, 1920, 1080), tolerancePhysical: 10);
        var baseline = h.ActionsRequested.Count;

        h.Hold(armX, armY, durationMs: 450);        // fixation onset ~300 ms arms the dwell
        h.Hold(glideX, glideY, durationMs: 1100);   // 40 px past the edge, well past the dwell

        Check.WithCustomMessage("an armed repeat run clicked after a glide past the edge.")
             .That(h.ActionsRequested.Skip(baseline)).IsEmpty();
        Check.That(h.State).IsEqualTo(InteractionState.RepeatMode);   // the mode itself survives
    }

    [Fact]
    public void RepeatMode_GlideOffScreenAndBack_DoesNotRefire_UntilAFreshFixation()
    {
        // C4's chosen rule is DISARM, not pause: the glided-away run must be re-aimed with a
        // fresh fixation. Coming back on-screen inside the same fixation does not re-fire.
        var h = new GazeFlowHarness(SelectionMode.None);
        h.Interaction.RepeatDwellMs = 400;
        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.LeftClick);
        h.Interaction.EnterRepeatMode(InteractionAction.RightClick);
        h.Interaction.SetRepeatScreenBounds(new PhysicalRect(0, 0, 1920, 1080), tolerancePhysical: 10);
        var baseline = h.ActionsRequested.Count;

        h.Hold(960, 1080, durationMs: 450);   // arm at the bottom edge
        h.Hold(960, 1120, durationMs: 500);   // glide 40 px past: disarm
        h.Hold(960, 1080, durationMs: 900);   // back on-screen, same fixation: no re-arm

        Check.WithCustomMessage("the disarmed run re-fired when the gaze returned on-screen.")
             .That(h.ActionsRequested.Skip(baseline)).IsEmpty();

        h.Hold(500, 500, durationMs: 1200);   // the gaze leaves: a fresh fixation arms and fires

        var fired = h.ActionsRequested.Skip(baseline).ToList();
        Check.That(fired).HasSize(1);
        Check.That(fired[0].Point.X).IsCloseTo(500, 60);
        Check.That(fired[0].Point.Y).IsCloseTo(500, 60);
    }

    [Fact]
    public void RepeatMode_InvalidSamples_DoNotAdvanceTheDwell()
    {
        // The held position of a lost-eyes gap is not evidence of where the user looks, so it
        // must neither advance nor fire the repeat dwell (the 2 s auto-exit is a separate path).
        var h = new GazeFlowHarness(SelectionMode.None);
        h.Interaction.RepeatDwellMs = 400;
        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.LeftClick);
        h.Interaction.EnterRepeatMode(InteractionAction.RightClick);
        h.Interaction.SetRepeatScreenBounds(new PhysicalRect(0, 0, 1920, 1080), tolerancePhysical: 10);
        var baseline = h.ActionsRequested.Count;

        h.Hold(800, 600, durationMs: 450);          // arms
        h.HoldInvalid(800, 600, durationMs: 700);   // eyes lost, well past the dwell, below the 2 s exit

        Check.WithCustomMessage("an invalid (held-position) sample advanced the repeat dwell.")
             .That(h.ActionsRequested.Skip(baseline)).IsEmpty();
        Check.That(h.State).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void RepeatMode_TheDwellCompletingSampleThatLeavesTheFixation_StillFires()
    {
        // F2 (review 01): the repeat call must run BEFORE _fixationDetector.Update. A sample that
        // completes the dwell AND leaves the 75 px fixation radius on-screen still fires at the
        // arm point, because the dwell advances before OnFixationEnded. Mutation: move the call
        // after the detector update → RED (the fixation end disarms the run first).
        var h = new GazeFlowHarness(SelectionMode.None);
        h.Interaction.RepeatDwellMs = 400;
        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.LeftClick);  // back to FreeLook
        h.Interaction.EnterRepeatMode(InteractionAction.RightClick);
        h.Interaction.SetRepeatScreenBounds(new PhysicalRect(0, 0, 1920, 1080), tolerancePhysical: 10);
        var baseline = h.ActionsRequested.Count;

        h.Hold(800, 600, durationMs: 450);   // onset ~330 ms arms at (800,600), ~100 ms of dwell
        h.Hold(800, 600, durationMs: 250);   // ~360 ms of dwell: one sample short
        Check.WithCustomMessage("the dwell fired before the completing sample.")
             .That(h.ActionsRequested.Skip(baseline)).IsEmpty();

        h.Gaze(1000, 600, dtMs: 60);         // >75 px jump; its timestamp crosses the 400 ms dwell

        var fired = h.ActionsRequested.Skip(baseline).ToList();
        Check.That(fired).HasSize(1);
        Check.That(fired[0].Action).IsEqualTo(InteractionAction.RightClick);
        Check.That(fired[0].Point.X).IsCloseTo(800, 60);
        Check.That(fired[0].Point.Y).IsCloseTo(600, 60);
    }

    [Fact]
    public void RepeatMode_TheGlideGateSeesTheCalibratedFilteredPoint()
    {
        // F3 (review 01): the gate must judge the point the fixation detector sees — calibrated
        // and filtered — not the raw sample. Here the raw sample is 180 px past the right edge and
        // the calibrated one 80 px past it, while the EMA-lagged filtered point is still on-screen:
        // only the filtered point fires. Mutants: feed `corrected` (calibrated, unfiltered) or the
        // raw `gaze` → both judge a point past the disarm band and never fire → RED.
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Smoothed;
        settings.Cursor.SmoothingFactor = 0.2;
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(
            provider,
            new RecordingInputSimulator(),
            settings,
            CalibrationWithOffset(dx: 100, dy: 0));
        engine.Start();
        var im = engine.InteractionManager;
        im.RepeatDwellMs = 600;
        im.SetRepeatScreenBounds(new PhysicalRect(0, 0, 1920, 1080), tolerancePhysical: 10);
        im.EnterRepeatMode(InteractionAction.LeftClick);
        var fired = new List<PhysicalPoint>();
        im.ActionRequested += (_, p, _) => fired.Add(p);

        // Raw (1820, 540) calibrates to (1720, 540); the EMA output is 1720 (constant input).
        long ts = FeedValidRaw(provider, 1820, 540, fromUs: 0, samples: 14);   // 450 ms, onset ~330
        Check.That(fired).IsEmpty();

        // One sample: raw (2100,540) → corrected (2000,540, past +30) but filtered 1776 (on-screen).
        // The 520 ms jump crosses the 600 ms dwell on this sample.
        provider.Push(new GazePoint(2100, 540, ts + 520_000, true));

        Check.WithCustomMessage("the gate judged the raw or unfiltered point, not the filtered one.")
             .That(fired).HasSize(1);
        Check.That(fired[0].X).IsCloseTo(1720, 0.5);
        Check.That(fired[0].Y).IsCloseTo(540, 0.5);
    }

    /// <summary>
    /// A calibration service whose fitted transform maps raw gaze to the screen by subtracting the
    /// given offset (targets are fed as raw = target + offset).
    /// </summary>
    private static CalibrationService CalibrationWithOffset(double dx, double dy)
    {
        var screen = new PhysicalPoint(1920, 1080);
        var service = new CalibrationService();
        service.StartSession(4, screen);
        var targets = CalibrationTargetPositionHelper.GetPositions(4, screen);
        for (int i = 0; i < targets.Count; i++)
        {
            service.BeginTarget(i, settleWindowMs: 0);
            for (int j = 0; j < 10; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + dx, targets[i].Y + dy));
            service.EndTarget();
        }
        Check.That(service.CompleteSession(screen, "DISPLAY1")).IsNotNull();
        return service;
    }

    /// <summary>Pushes <paramref name="samples"/> valid raw samples 33 ms apart; returns the last timestamp.</summary>
    private static long FeedValidRaw(
        ScriptedGazeProvider provider, double x, double y, long fromUs, int samples, int stepUs = 33_000)
    {
        long ts = fromUs;
        for (int i = 0; i < samples; i++)
        {
            ts += stepUs;
            provider.Push(new GazePoint(x, y, ts, true));
        }
        return ts;
    }

    [Fact]
    public void Pause_SuppressesCursorMovement_AndTogglesBack()
    {
        var h = new GazeFlowHarness();

        h.Interaction.TogglePause();
        Check.That(h.State).IsEqualTo(InteractionState.Paused);

        var movesBefore = h.Input.MoveCount;
        h.Hold(800, 600, durationMs: 300); // gaze while paused must NOT move the cursor or fixate
        Check.That(h.Input.MoveCount).IsEqualTo(movesBefore);
        Check.That(h.State).IsEqualTo(InteractionState.Paused);

        h.Interaction.TogglePause();
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void ManualSelectionBack_StepsMenuToMagnifier_ThenMagnifierToFreeLook()
    {
        var h = new GazeFlowHarness(SelectionMode.ManualSelection);

        h.FixateAndActivate(700, 300);
        h.Interaction.OnManualSelectionDwell(new PhysicalPoint(710, 305));
        Check.That(h.State).IsEqualTo(InteractionState.ManualSelectionActionMenuVisible);

        h.Interaction.OnManualSelectionBack(); // back from action menu → magnifier
        Check.That(h.State).IsEqualTo(InteractionState.ManualSelectionActive);

        h.Interaction.OnManualSelectionBack(); // back again → close to free look
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
    }
}
