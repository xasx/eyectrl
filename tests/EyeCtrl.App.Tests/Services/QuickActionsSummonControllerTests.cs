namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;

public class QuickActionsSummonControllerTests
{
    private QuickActionsSummonController CreateController(
        string edge = "Left", double screenW = 1920, double screenH = 1080,
        int summonMs = 400, int dismissMs = 5000, Func<bool>? calibrationActive = null,
        Func<bool>? holdDismiss = null)
    {
        return new QuickActionsSummonController(
            edge, screenW, screenH, summonMs, dismissMs,
            calibrationActive: calibrationActive ?? (() => false),
            holdDismiss: holdDismiss);
    }

    // NOTE: Feed takes a GazePoint (packs X, Y, TimestampMicroseconds, IsValid),
    // matching ToolbarAutoHideController.Feed(GazePoint, long). Tests build the
    // GazePoint with a distinct wall-clock timestampUs arg.
    private static GazePoint Pt(double x, double y, long tsUs, bool valid = true)
        => new(x, y, tsUs, valid);

    [Fact]
    public void Constructor_StateIsIdle()
    {
        var c = CreateController();
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void Feed_ValidGazePastLeftEdge_SummonsAfterThreshold()
    {
        var c = CreateController(edge: "Left", screenW: 1920, summonMs: 400);
        int? toggles = null;
        c.SummonRequested += () => toggles = (toggles ?? 0) + 1;

        // Gaze at (-10, 540) = past left edge, valid
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(toggles).IsNull();

        // Enough time passes
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
        Check.That(toggles).IsEqualTo(1);
    }

    [Fact]
    public void Feed_InvalidGaze_DoesNotSummon()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        int? toggles = null;
        c.SummonRequested += () => toggles = (toggles ?? 0) + 1;

        c.Feed(Pt(-10, 540, 0, valid: false), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000, valid: false), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(toggles).IsNull();
    }

    [Fact]
    public void Feed_GazeInsideScreen_DoesNotSummon()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        c.Feed(Pt(100, 540, 0), timestampUs: 0);
        c.Feed(Pt(100, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void Feed_GazePastWrongEdge_DoesNotSummon()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        // Past right edge
        c.Feed(Pt(1930, 540, 0), timestampUs: 0);
        c.Feed(Pt(1930, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void Feed_DismissTimeout_FiresAfterGazeAway()
    {
        var c = CreateController(edge: "Left", summonMs: 400, dismissMs: 5000);
        int? dismissals = 0;
        int? summons = 0;
        c.SummonRequested += () => summons++;
        c.DismissRequested += () => dismissals++;

        // Summon
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
        Check.That(summons).IsEqualTo(1);

        // Gaze returns inside — after dismiss timeout, controller fires DismissRequested + goes Idle
        c.Feed(Pt(100, 540, 600_000), timestampUs: 600_000);
        c.Feed(Pt(100, 540, 6_000_000), timestampUs: 6_000_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(dismissals).IsEqualTo(1);
    }

    [Fact]
    public void EnterSummoned_DoesNotAutoDismiss_AfterTimeout()
    {
        // Regression guard: a grid opened via the toolbar button (EnterSummoned path, NOT a
        // hot-edge summon) must NOT auto-dismiss after DismissTimeoutMs of normal on-screen gaze.
        // Only hot-edge-summoned sessions apply the gaze-away dismiss timer.
        var c = CreateController(edge: "Left", screenW: 1920, summonMs: 400, dismissMs: 5000);
        int dismissals = 0;
        c.DismissRequested += () => dismissals++;

        // Simulate the toolbar-button path: VM shows the grid, coordinator calls EnterSummoned.
        c.EnterSummoned();
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        // Gaze on-screen (not past edge) well past the dismiss timeout → must NOT dismiss.
        c.Feed(Pt(100, 540, 0), timestampUs: 0);
        c.Feed(Pt(100, 540, 10_000_000), timestampUs: 10_000_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
        Check.That(dismissals).IsEqualTo(0);

        // A blink does NOT dismiss (ignored like ToolbarAutoHideController); the grid stays open.
        c.Feed(Pt(100, 540, 11_000_000, valid: false), timestampUs: 11_000_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
        Check.That(dismissals).IsEqualTo(0);
    }

    [Fact]
    public void Feed_BlinkDuringSummonRun_ResetsRun()
    {
        var c = CreateController(edge: "Left", summonMs: 400);

        // Start summon run
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 200_000), timestampUs: 200_000);

        // Blink (invalid)
        c.Feed(Pt(-10, 540, 250_000, valid: false), timestampUs: 250_000);

        // Resume — run should restart, not continue
        c.Feed(Pt(-10, 540, 300_000), timestampUs: 300_000);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        // Only 200ms accumulated after blink, not enough for 400ms threshold
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void SetSuspended_PreventsSummonAndForcesIdle()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        // Summon first
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        c.SetSuspended("test", true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);

        // Should not re-summon while suspended
        c.Feed(Pt(-10, 540, 600_000), timestampUs: 600_000);
        c.Feed(Pt(-10, 540, 1_100_000), timestampUs: 1_100_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void NamedClaims_AnExerciseEnding_CannotLiftTheKeyboardClaim()
    {
        // B7 fixup-04 L1: the suspension used to be ONE shared bool. The exercise is a third
        // writer, so its (false) on break end re-enabled the hot-edge summon for the rest of a
        // still-open keyboard session. Named claims: live only when no claim holds.
        var c = CreateController(edge: "Left", summonMs: 400);
        int summons = 0;
        c.SummonRequested += () => summons++;

        c.SetSuspended("keyboard", true);
        c.SetSuspended("exercise", true);
        c.SetSuspended("exercise", false);          // the exercise ends; the keyboard is still up

        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.WithCustomMessage("the exercise's release lifted the keyboard's claim")
            .That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(summons).IsEqualTo(0);

        // Releasing the keyboard resumes the summon normally.
        c.SetSuspended("keyboard", false);
        c.Feed(Pt(-10, 540, 600_000), timestampUs: 600_000);
        c.Feed(Pt(-10, 540, 1_100_000), timestampUs: 1_100_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
        Check.That(summons).IsEqualTo(1);
    }

    [Fact]
    public void NamedClaims_SettingsAndExercise_AreIndependent()
    {
        // The older keyboard-vs-Settings race, closed by the same claims.
        var c = CreateController();
        c.SetSuspended("settings", true);
        c.SetSuspended("exercise", true);
        c.SetSuspended("settings", false);
        Check.That(c.IsSuspended).IsTrue();
        c.SetSuspended("exercise", false);
        Check.That(c.IsSuspended).IsFalse();
    }

    [Fact]
    public void ReleasingAClaimYouDoNotHold_ChangesNothing()
    {
        // B7 fixup-05 L-a (review 04 K6): a release removes only the caller's own claim. Settings
        // releases on two paths; a release that cleared the set would lift the keyboard's claim.
        var c = CreateController();
        c.SetSuspended("keyboard", true);
        c.SetSuspended("settings", false);            // not held

        Check.WithCustomMessage("releasing an unheld claim lifted another claimant's suspension")
            .That(c.IsSuspended).IsTrue();

        c.SetSuspended("keyboard", false);
        Check.That(c.IsSuspended).IsFalse();
    }

    [Fact]
    public void ConcurrentClaims_FromTwoThreads_LeaveAConsistentState()
    {
        // B7 fixup-05 L-c: "exercise" is written on the eye-care thread while "keyboard"/"settings"
        // arrive on the UI thread. The claim set is locked; the source pin below is the
        // deterministic mutant kill (HashSet corruption is not reproducible on demand).
        var c = CreateController();
        var errors = new List<Exception>();
        void Hammer(string claim)
        {
            try
            {
                for (var i = 0; i < 5000; i++)
                {
                    c.SetSuspended(claim, true);
                    c.SetSuspended(claim, false);
                }
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(ex);
            }
        }

        var keyboard = new Thread(() => Hammer("keyboard"));
        var exercise = new Thread(() => Hammer("exercise"));
        keyboard.Start();
        exercise.Start();
        keyboard.Join();
        exercise.Join();

        Check.That(errors).IsEmpty();
        Check.That(c.IsSuspended).IsFalse();
    }

    [Fact]
    public void SetSuspended_LocksTheClaimSet_AndRaisesTheDismissOutsideIt()
    {
        // B7 fixup-05 L-c's deterministic pin: the claim set must be mutated under the lock, and
        // ResetToIdleCore (which raises DismissRequested) must sit outside it so a subscriber can
        // never deadlock against the lock.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "QuickActionsSummonController.cs"))),
            @"\s+", string.Empty);

        var lockIndex = source.IndexOf("lock(_claimsLock)", StringComparison.Ordinal);
        Check.WithCustomMessage("SetSuspended mutates the claim set without a lock")
            .That(lockIndex).IsStrictlyGreaterThan(-1);

        var bodyStart = source.IndexOf('{', lockIndex);
        var bodyEnd = source.IndexOf('}', bodyStart);
        var inLock = source[(bodyStart + 1)..bodyEnd];
        Check.WithCustomMessage("DismissRequested is raised inside the lock")
            .That(inLock.Contains("ResetToIdleCore")).IsFalse();
        Check.That(source.IndexOf("ResetToIdleCore(notify:true)", lockIndex, StringComparison.Ordinal))
            .IsStrictlyGreaterThan(bodyEnd);
    }

    [Fact]
    public void CalibrationActive_Gate_PreventsSummon()
    {
        bool calibrationActive = true;
        var c = CreateController(calibrationActive: () => calibrationActive);
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void EnterSummoned_SetsState()
    {
        var c = CreateController();
        c.EnterSummoned();
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
    }

    [Fact]
    public void ResetToIdle_SetsState()
    {
        var c = CreateController();
        c.EnterSummoned();
        c.ResetToIdle();
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void UpdateEdge_WhileSummoned_DismissesAndSwapsEdge()
    {
        var c = CreateController(edge: "Left", screenW: 1920, summonMs: 400);
        int dismissals = 0;
        c.DismissRequested += () => dismissals++;

        // Summon via left edge
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        // Swap to right edge while summoned → should dismiss + fire event
        c.UpdateEdge("Right");
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(dismissals).IsEqualTo(1);

        // Left edge no longer summons (proves the swap took effect)
        c.Feed(Pt(-10, 540, 600_000), timestampUs: 600_000);
        c.Feed(Pt(-10, 540, 1_100_000), timestampUs: 1_100_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);

        // Right edge now summons
        c.Feed(Pt(1930, 540, 1_200_000), timestampUs: 1_200_000);
        c.Feed(Pt(1930, 540, 1_700_000), timestampUs: 1_700_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
    }

    [Fact]
    public void Feed_BlinkWhileSummoned_DoesNotDismiss()
    {
        // A blink (involuntary and frequent for eye-tracker users) must NOT close the grid, or it
        // would vanish mid-use and abort an in-progress tile dwell. Matches ToolbarAutoHideController,
        // which ignores blinks. Only calibration / suspension / gaze-away timeout / activation dismiss.
        var c = CreateController(edge: "Left", summonMs: 400, dismissMs: 5000);
        int dismissals = 0;
        c.DismissRequested += () => dismissals++;

        // Summon first
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        // Blink (invalid sample) while summoned → ignored; stays Summoned
        c.Feed(Pt(-10, 540, 600_000, valid: false), timestampUs: 600_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
        Check.That(dismissals).IsEqualTo(0);
    }

    [Fact]
    public void Feed_CalibrationWhileSummoned_Dismisses()
    {
        bool calibrating = false;
        var c = CreateController(edge: "Left", summonMs: 400, dismissMs: 5000,
            calibrationActive: () => calibrating);
        int dismissals = 0;
        c.DismissRequested += () => dismissals++;

        // Summon first
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        // Calibration starts → next sample tears the grid down (suppression, unlike a blink).
        calibrating = true;
        c.Feed(Pt(100, 540, 600_000), timestampUs: 600_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(dismissals).IsEqualTo(1);
    }

    [Fact]
    public void Feed_BlinkDuringDismissCountdown_DoesNotResetTimer()
    {
        // A blink partway through the gaze-away countdown must not reset it — the grid should still
        // dismiss on schedule (blink samples are ignored, timer frozen, not restarted).
        var c = CreateController(edge: "Left", summonMs: 400, dismissMs: 5000);
        int dismissals = 0;
        c.DismissRequested += () => dismissals++;

        // Hot-edge summon
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        // Gaze on-screen starts the dismiss countdown at t=600ms
        c.Feed(Pt(100, 540, 600_000), timestampUs: 600_000);
        // Blink partway through — must NOT reset the countdown
        c.Feed(Pt(100, 540, 3_000_000, valid: false), timestampUs: 3_000_000);
        // >5s after the countdown started → dismisses
        c.Feed(Pt(100, 540, 5_700_000), timestampUs: 5_700_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(dismissals).IsEqualTo(1);
    }

    [Fact]
    public void HoldDismiss_KeepsAHotEdgeGridOpen_WhileATileDwellProgresses()
    {
        // B8b F1: the gaze-away timeout may not expire while a grid target is mid-dwell. The gaze
        // is ON the grid (inside the screen, so the timer branch runs) for far longer than
        // DismissTimeoutMs; the hold keeps the grid up, and the countdown restarts fresh once it
        // releases. Without the hold this is exactly the summon-and-vanish the owner hit.
        bool holding = false;
        var c = CreateController(edge: "Left", summonMs: 400, dismissMs: 5000,
            holdDismiss: () => holding);
        int dismissals = 0;
        c.DismissRequested += () => dismissals++;

        // Hot-edge summon
        c.Feed(Pt(-10, 540, 0), timestampUs: 0);
        c.Feed(Pt(-10, 540, 500_000), timestampUs: 500_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        // A tile dwell runs on the grid for 6 s — 1 s longer than the dismiss timeout.
        holding = true;
        for (long t = 600_000; t <= 6_600_000; t += 100_000)
            c.Feed(Pt(100, 540, t), timestampUs: t);
        Check.WithCustomMessage("the dismiss timeout fired while a tile dwell was in progress")
            .That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
        Check.That(dismissals).IsEqualTo(0);

        // Dwell finished: the full timeout runs again from here, then the grid dismisses.
        holding = false;
        c.Feed(Pt(100, 540, 6_700_000), timestampUs: 6_700_000);
        c.Feed(Pt(100, 540, 11_800_000), timestampUs: 11_800_000);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(dismissals).IsEqualTo(1);
    }
}
