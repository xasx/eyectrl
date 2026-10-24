namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Locked-action dwell-to-act semantics (design D23-D28): the Modes menu picks the action;
/// a fixation arms a dwell at its point; VALID samples advance it; it fires ONCE per fixation;
/// fixation end cancels; chrome suppression zones never arm; sustained lost gaze exits the mode.
/// Timing is driven by calling NoteGazeSample directly with synthetic timestamps - the same
/// contract GazeEngine fulfils on the real gaze thread.
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
        im.NoteGazeSample(true, 10 * Ms + 599 * Ms);                 // 1 ms short: no fire
        Check.That(fired).IsEmpty();

        im.NoteGazeSample(true, 10 * Ms + 601 * Ms);                 // past 600 ms: fire
        Check.That(fired).HasSize(1);
        Check.That(fired[0].A).IsEqualTo(InteractionAction.RightClick);
        Check.That(fired[0].P.X).IsCloseTo(300, 1e-6);
        Check.That(fired[0].P.Y).IsCloseTo(400, 1e-6);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);  // stays in mode
    }

    [Fact]
    public void OneFirePerFixation_HoldingLongerDoesNotRefire()
    {
        var im = Armed(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(300, 400));
        im.NoteGazeSample(true, 10 * Ms + 700 * Ms);
        im.NoteGazeSample(true, 10 * Ms + 5700 * Ms);   // keep staring
        Check.That(fired).IsEqualTo(1);
    }

    [Fact]
    public void NewFixation_RearmsAfterAFire()
    {
        var im = Armed(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(300, 400));
        im.NoteGazeSample(true, 10 * Ms + 700 * Ms);
        im.OnFixationEnded();

        im.OnFixationStarted(new PhysicalPoint(700, 500));           // fresh fixation re-arms
        im.NoteGazeSample(true, 10 * Ms + 1400 * Ms);
        Check.That(fired).IsEqualTo(2);
    }

    [Fact]
    public void FixationEnd_CancelsTheArmedDwell()
    {
        var im = Armed(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(300, 400));
        im.NoteGazeSample(true, 10 * Ms + 400 * Ms);
        im.OnFixationEnded();
        im.NoteGazeSample(true, 10 * Ms + 800 * Ms);   // late samples, no arm
        Check.That(fired).IsEqualTo(0);
    }

    [Fact]
    public void InvalidSamples_DoNotAdvanceTheDwell()
    {
        var im = Armed(InteractionAction.LeftClick);
        var fired = 0;
        im.ActionRequested += (_, _, _) => fired++;

        im.OnFixationStarted(new PhysicalPoint(300, 400));
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
        im.NoteGazeSample(true, 10 * Ms + 1000 * Ms);
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
        im.NoteGazeSample(true, 10 * Ms + 300 * Ms);   // elapsed 300ms of 600 → 0.5
        im.NoteGazeSample(true, 10 * Ms + 610 * Ms);   // fire → stays contracted at 1 (round 3 F6)

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
        // A5 (b) at the Core contract level: the repeat stop runs first (the owner's hook), then
        // the state flips to Paused — and the locked action is gone, so nothing is re-armed into
        // the paused state.
        var im = Armed(InteractionAction.LeftClick);
        var events = new List<string>();
        im.StateChanged += s => events.Add(s.ToString());
        im.ExitRepeatModeForPause += () =>
        {
            events.Add("exit-repeat");
            im.ExitRepeatMode();
        };

        im.TogglePause();

        Check.That(events).ContainsExactly("exit-repeat", "FreeLook", "Paused");
        Check.That(im.CurrentState).IsEqualTo(InteractionState.Paused);
        Check.That(im.RepeatAction).IsNull();
    }
}
