namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Locked-action dwell-to-act semantics (design D23-D28): the Modes menu picks the action;
/// a fixation arms a dwell at its point; VALID samples advance it — and only while their filtered
/// position is on-screen (C4) — it fires ONCE per fixation; fixation end cancels; chrome
/// suppression zones never arm; sustained lost gaze exits the mode. Timing is driven by calling
/// NoteGazeSample (liveness) and NoteRepeatGazeSample (the filtered position) directly with
/// synthetic timestamps - the same contract GazeEngine fulfils on the real gaze thread.
/// </summary>
public class InteractionManagerRepeatModeTests
{
    private const long Ms = 1_000;

    private static InteractionManager Armed(InteractionAction action)
    {
        var im = new InteractionManager { RepeatDwellMs = 600 };
        im.NoteGazeSample(true, 10 * Ms);   // liveness before entry
        im.EnterRepeatMode(action);
        return im;
    }

    private static InteractionManager WithScreenBounds(InteractionAction action)
    {
        var im = Armed(action);
        im.SetRepeatScreenBounds(new PhysicalRect(0, 0, 1920, 1080), tolerancePhysical: 10);
        return im;
    }

    [Fact]
    public void EnterRepeatMode_WithClickAction_FromFreeLook_EntersWithThatAction()
    {
        var im = Armed(InteractionAction.RightClick);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
        Check.That(im.RepeatAction).IsEqualTo(InteractionAction.RightClick);
    }

    [Fact]
    public void EnterRepeatMode_IsIgnoredOutsideFreeLook()
    {
        var im = new InteractionManager();
        im.OnFixationStarted(new PhysicalPoint(1000, 800));
        im.OnActivationButtonDwelled();   // ActionMenuVisible
        im.EnterRepeatMode(InteractionAction.LeftClick);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);

        var paused = new InteractionManager();
        paused.TogglePause();
        paused.EnterRepeatMode(InteractionAction.LeftClick);
        Check.That(paused.CurrentState).IsEqualTo(InteractionState.Paused);
    }

    [Fact]
    public void EnterRepeatMode_RefusesNonClickActions()
    {
        var im = new InteractionManager();
        im.EnterRepeatMode(InteractionAction.ScrollUp);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void HeldGaze_FiresTheLockedActionAtTheFixationPoint_AfterTheDwell()
    {
        var im = Armed(InteractionAction.RightClick);
        var fired = new List<(InteractionAction A, PhysicalPoint P)>();
        im.ActionRequested += (a, p, _) => fired.Add((a, p));

        im.OnFixationStarted(new PhysicalPoint(300, 400));           // arms at lastSample=10ms
        im.NoteRepeatGazeSample(new PhysicalPoint(300, 400), 10 * Ms + 599 * Ms);   // 1 ms short: no fire
        Check.That(fired).IsEmpty();

        im.NoteRepeatGazeSample(new PhysicalPoint(300, 400), 10 * Ms + 601 * Ms);   // past 600 ms: fire
        Check.That(fired).HasSize(1);
        Check.That(fired[0].A).IsEqualTo(InteractionAction.RightClick);
        Check.That(fired[0].P.X).IsCloseTo(300, 1e-6);
        Check.That(fired[0].P.Y).IsCloseTo(400, 1e-6);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);  // stays in mode
    }

    [Fact]
    public void NoteGazeSample_Alone_NeverFiresTheDwell()
    {
        // C4: the position-free path is dead. The dwell advances ONLY on
        // NoteRepeatGazeSample, which carries the calibrated, filtered position.
        // Mutation: restore the advance inside NoteGazeSample → RED (it fires at a stale point).
        var im = Armed(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(300, 400));
        im.NoteGazeSample(true, 10 * Ms + 2_000 * Ms);   // valid, far past the dwell, no position

        Check.WithCustomMessage("the position-free NoteGazeSample path fired the repeat dwell.")
             .That(fired).IsEqualTo(0);
    }

    [Fact]
    public void OneFirePerFixation_HoldingLongerDoesNotRefire()
    {
        var im = Armed(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(300, 400));
        im.NoteRepeatGazeSample(new PhysicalPoint(300, 400), 10 * Ms + 700 * Ms);
        im.NoteRepeatGazeSample(new PhysicalPoint(300, 400), 10 * Ms + 5700 * Ms);   // keep staring
        Check.That(fired).IsEqualTo(1);
    }

    [Fact]
    public void NewFixation_RearmsAfterAFire()
    {
        var im = Armed(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(300, 400));
        im.NoteRepeatGazeSample(new PhysicalPoint(300, 400), 10 * Ms + 700 * Ms);
        im.OnFixationEnded();

        im.OnFixationStarted(new PhysicalPoint(700, 500));           // fresh fixation re-arms
        im.NoteRepeatGazeSample(new PhysicalPoint(700, 500), 10 * Ms + 1400 * Ms);
        Check.That(fired).IsEqualTo(2);
    }

    [Fact]
    public void FixationEnd_CancelsTheArmedDwell()
    {
        var im = Armed(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(300, 400));
        im.NoteRepeatGazeSample(new PhysicalPoint(300, 400), 10 * Ms + 400 * Ms);
        im.OnFixationEnded();
        im.NoteRepeatGazeSample(new PhysicalPoint(300, 400), 10 * Ms + 800 * Ms);   // late sample, no arm
        Check.That(fired).IsEqualTo(0);
    }

    [Fact]
    public void InvalidSamples_DoNotAdvanceTheDwell()
    {
        var im = Armed(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(300, 400));
        // GazeEngine only feeds NoteRepeatGazeSample for valid (non-gap) samples; the invalid
        // sample itself must never fire, and its held position is not evidence.
        im.NoteGazeSample(false, 10 * Ms + 700 * Ms);   // eyes lost: dwell frozen
        Check.That(fired).IsEqualTo(0);
    }

    [Fact]
    public void SuppressionZone_FixationNeverArms()
    {
        var im = Armed(InteractionAction.LeftClick);
        im.SetRepeatSuppressionZones(new PhysicalRect(0, 0, 1920, 250));   // toolbar band
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(960, 120));              // inside the band
        im.NoteRepeatGazeSample(new PhysicalPoint(960, 120), 10 * Ms + 1000 * Ms);
        Check.That(fired).IsEqualTo(0);
    }

    [Fact]
    public void SustainedLostGaze_ExitsTheMode()
    {
        var im = Armed(InteractionAction.LeftClick);
        im.OnFixationStarted(new PhysicalPoint(300, 400));

        im.NoteGazeSample(false, 10 * Ms + 2500 * Ms);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(im.RepeatAction).IsNull();
    }

    [Fact]
    public void DwellProgress_Publishes0To1AndBack()
    {
        var im = Armed(InteractionAction.LeftClick);
        var progress = new List<double>();
        im.RepeatDwellProgressChanged += p => progress.Add(p);

        im.OnFixationStarted(new PhysicalPoint(300, 400));
        im.NoteRepeatGazeSample(new PhysicalPoint(300, 400), 10 * Ms + 300 * Ms);   // 300ms of 600 → 0.5
        im.NoteRepeatGazeSample(new PhysicalPoint(300, 400), 10 * Ms + 610 * Ms);   // fire → stays contracted at 1

        Check.That(progress[0]).IsCloseTo(0.5, 0.01);
        Check.That(progress).Contains(1);
        Check.That(progress.Last()).IsEqualTo(1);      // held stare cannot re-fire: stays "spent"

        im.OnFixationEnded();                          // gaze moves off → resets to ready
        Check.That(progress.Last()).IsEqualTo(0);
    }

    [Fact]
    public void ExitRepeatMode_ReturnsToFreeLook_ClearingTheLockedAction()
    {
        var im = Armed(InteractionAction.MiddleClick);
        im.ExitRepeatMode();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(im.RepeatAction).IsNull();
    }

    [Fact]
    public void TogglePause_InRepeatMode_ExitsRepeatThenPauses()
    {
        // A5 (b) at the Core contract level: the repeat stop (the state-only exit the stop
        // button and ESC also reach) runs first, then the state flips to Paused — and the locked
        // action is gone (F8/M7: a bare manager must not carry its payload into Paused).
        var im = Armed(InteractionAction.LeftClick);
        var events = new List<string>();
        im.StateChanged += s => events.Add(s.ToString());

        im.TogglePause();

        Check.That(events).ContainsExactly("FreeLook", "Paused");
        Check.That(im.CurrentState).IsEqualTo(InteractionState.Paused);
        Check.That(im.RepeatAction).IsNull();
    }

    [Fact]
    public void OffScreenFixation_NeverArmsTheRepeatDwell()
    {
        // A5 fixup-03 H2: past the screen edge is the toolbar/Quick-Actions summon gesture. A
        // repeat click armed there is clamped by the OS onto the edge row (a tab strip or title
        // bar). Only the arm is refused; an on-screen fixation still fires normally.
        var im = WithScreenBounds(InteractionAction.LeftClick);
        var fired = new List<(InteractionAction A, PhysicalPoint P)>();
        im.ActionRequested += (a, p, _) => fired.Add((a, p));

        im.OnFixationStarted(new PhysicalPoint(960, -100));   // the summon gesture
        im.NoteRepeatGazeSample(new PhysicalPoint(960, -100), 10 * Ms + 2_000 * Ms);

        Check.WithCustomMessage("an off-screen fixation armed the repeat dwell.")
             .That(fired).IsEmpty();

        // Control: on-screen still fires, at its own point.
        im.OnFixationEnded();
        im.OnFixationStarted(new PhysicalPoint(960, 540));
        im.NoteRepeatGazeSample(new PhysicalPoint(960, 540), 10 * Ms + 4_000 * Ms);

        Check.That(fired).HasSize(1);
        Check.That(fired[0].P.X).IsCloseTo(960, 1e-6);
        Check.That(fired[0].P.Y).IsCloseTo(540, 1e-6);
    }

    [Fact]
    public void AFixationJustPastTheEdge_InsideTheTolerance_StillArms()
    {
        // T1: the tolerance keeps the border usable. 5 px above the screen is still an ordinary
        // on-screen fixation (tracker jitter at the edge row), so it must arm and fire.
        var im = WithScreenBounds(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(960, -5));
        im.NoteRepeatGazeSample(new PhysicalPoint(960, -5), 10 * Ms + 2_000 * Ms);

        Check.WithCustomMessage("a fixation 5 px past the edge (inside the tolerance) did not arm.")
             .That(fired).IsEqualTo(1);
    }

    [Fact]
    public void WithNoScreenBoundsPublished_AnOffScreenFixationStillArms()
    {
        // Pre-fix contract for a bare manager: nothing published means nothing refused.
        var im = Armed(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(960, -100));
        im.NoteRepeatGazeSample(new PhysicalPoint(960, -100), 10 * Ms + 2_000 * Ms);

        Check.That(fired).IsEqualTo(1);
    }

    [Theory]
    [InlineData(960, 1080, 960, 1120)]   // 40 px past the bottom edge
    [InlineData(0, 540, -40, 540)]       // 40 px past the left edge
    [InlineData(1920, 540, 1960, 540)]   // 40 px past the right edge
    public void AnOffScreenGlide_DisarmsTheArmedRun_AndNeverFires(
        double armX, double armY, double glideX, double glideY)
    {
        // C4: an armed run glided ~40 px past a band-less edge must not click at its stale
        // on-screen point — the fixation detector still holds (radius 75 px), so only the
        // position gate can stop it. DISARM is the chosen rule (A5 fixup-03 parity): the run must
        // be re-aimed with a fresh fixation, so gliding back on-screen does NOT re-fire.
        // Mutation: remove the off-screen gate → RED (a click fires at the arm point).
        var im = WithScreenBounds(InteractionAction.LeftClick);
        var fired = new List<PhysicalPoint>();
        var progress = new List<double>();
        im.ActionRequested += (_, p, _) => fired.Add(p);
        im.RepeatDwellProgressChanged += progress.Add;
        var arm = new PhysicalPoint(armX, armY);
        var glide = new PhysicalPoint(glideX, glideY);

        im.OnFixationStarted(arm);
        im.NoteRepeatGazeSample(arm, 10 * Ms + 300 * Ms);        // armed, half-way
        im.NoteRepeatGazeSample(glide, 10 * Ms + 1_200 * Ms);    // past the edge, past the dwell

        Check.WithCustomMessage("an off-screen glide fired the repeat click.")
             .That(fired).IsEmpty();
        Check.WithCustomMessage("disarming must return the cursor to ready.")
             .That(progress.Last()).IsEqualTo(0);

        // DISARM rule: returning on-screen within the same fixation does not re-fire.
        im.NoteRepeatGazeSample(arm, 10 * Ms + 2_400 * Ms);
        Check.WithCustomMessage("the disarmed run re-fired when the gaze returned on-screen.")
             .That(fired).IsEmpty();

        // A fresh fixation re-arms and fires, proving the mode (and the run) is still usable.
        im.OnFixationEnded();
        im.OnFixationStarted(arm);
        im.NoteRepeatGazeSample(arm, 10 * Ms + 3_000 * Ms);
        Check.That(fired).HasSize(1);
        Check.That(fired[0].X).IsCloseTo(armX, 1e-6);
        Check.That(fired[0].Y).IsCloseTo(armY, 1e-6);
    }

    [Fact]
    public void TheFiringSample_IsJudgedOnItsOwnPosition()
    {
        // C4: the sample that crosses the dwell bar is judged on ITS OWN position. The sample
        // that ends a fixation can still arrive here (NoteRepeatGazeSample runs before the
        // detector update), so an off-screen one must not fire the stale target.
        // Mutation: check the ARM point instead of the sample point → RED.
        var im = WithScreenBounds(InteractionAction.LeftClick);
        var fired = 0;
        var progress = new List<double>();
        im.ActionRequested += (_, _, _) => fired++;
        im.RepeatDwellProgressChanged += progress.Add;

        im.OnFixationStarted(new PhysicalPoint(960, 1080));
        im.NoteRepeatGazeSample(new PhysicalPoint(960, 1080), 10 * Ms + 599 * Ms);   // 1 ms short
        im.NoteRepeatGazeSample(new PhysicalPoint(960, 1120), 10 * Ms + 601 * Ms);  // crosses, off-screen

        Check.WithCustomMessage("the firing sample's own off-screen position was not judged.")
             .That(fired).IsEqualTo(0);
        Check.That(progress.Last()).IsEqualTo(0);
    }

    [Fact]
    public void AnOffScreenGlide_AfterTheFire_KeepsTheSpentCursor()
    {
        // Round 3 finding 6: a fired run's cursor stays contracted until the run ends. Going
        // off-screen after the fire must not reset that (nothing can re-fire on this fixation).
        var im = WithScreenBounds(InteractionAction.LeftClick);
        var fired = 0;
        var progress = new List<double>();
        im.ActionRequested += (_, _, _) => fired++;
        im.RepeatDwellProgressChanged += progress.Add;

        im.OnFixationStarted(new PhysicalPoint(960, 540));
        im.NoteRepeatGazeSample(new PhysicalPoint(960, 540), 10 * Ms + 700 * Ms);
        Check.That(fired).IsEqualTo(1);
        Check.That(progress.Last()).IsEqualTo(1);

        im.NoteRepeatGazeSample(new PhysicalPoint(960, 1120), 10 * Ms + 900 * Ms);
        Check.That(fired).IsEqualTo(1);
        Check.That(progress.Last()).IsEqualTo(1);

        im.OnFixationEnded();
        Check.That(progress.Last()).IsEqualTo(0);
    }

    [Fact]
    public void AnOffScreenGlide_InsideTheTolerance_StillFires()
    {
        // T1 for the glide gate: 5 px past the edge is tracker jitter at the edge row, not a
        // summon gesture. It must keep advancing and fire (a zero-tolerance gate would fail).
        var im = WithScreenBounds(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(960, 1085));   // 5 px past the bottom, inside 10 px
        im.NoteRepeatGazeSample(new PhysicalPoint(960, 1085), 10 * Ms + 2_000 * Ms);

        Check.WithCustomMessage("a sample 5 px past the edge (inside the tolerance) did not fire.")
             .That(fired).IsEqualTo(1);
    }
}
