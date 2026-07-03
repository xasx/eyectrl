namespace EyeCtrl.EyeCare.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class EyeCareSchedulerTests
{
    private static readonly EyeCareSettings Cfg = new()
    {
        Enabled = true,
        IntervalMinutes = 1,         // 60s for fast tests
        NaturalBreakSeconds = 30,
        SnoozeMinutes = 1
    };

    [Fact]
    public void ContinuousGaze_FiresBreak_AtInterval()
    {
        var scheduler = new EyeCareScheduler(Cfg);
        bool fired = false;
        scheduler.BreakDue += () => fired = true;

        // 1 minute of continuous valid gaze, 1s steps
        for (long t = 0; t <= 60_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        Check.That(fired).IsTrue();
    }

    [Fact]
    public void Paused_DoesNotFireBreak()
    {
        var scheduler = new EyeCareScheduler(Cfg);
        bool fired = false;
        scheduler.BreakDue += () => fired = true;

        for (long t = 0; t <= 60_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: true);

        Check.That(fired).IsFalse();
    }

    [Fact]
    public void NaturalBreak_SuppressesScheduledBreak()
    {
        var scheduler = new EyeCareScheduler(Cfg);
        bool fired = false;
        scheduler.BreakDue += () => fired = true;

        // First 35s: look away (>= NaturalBreakSeconds=30) → natural break taken
        for (long t = 0; t < 35_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(0, 0, t, IsValid: false), isPaused: false);
        // Then continue to the interval
        for (long t = 35_000_000; t <= 60_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        Check.That(fired).IsFalse();
    }

    [Fact]
    public void Disabled_NeverFires()
    {
        var cfg = new EyeCareSettings { Enabled = false };
        var scheduler = new EyeCareScheduler(cfg);
        bool fired = false;
        scheduler.BreakDue += () => fired = true;

        for (long t = 0; t <= 60_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        Check.That(fired).IsFalse();
    }

    [Fact]
    public void AwayGap_DoesNotLeakOntoActiveClock()
    {
        var scheduler = new EyeCareScheduler(Cfg); // interval 60s, NaturalBreakSeconds=30
        bool fired = false;
        scheduler.BreakDue += () => fired = true;

        // 30s active gaze
        for (long t = 0; t <= 30_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);
        // 20s away (invalid) — under NaturalBreakSeconds so NO suppression; isolates the leak
        for (long t = 31_000_000; t <= 50_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(0, 0, t, IsValid: false), isPaused: false);
        // 20s more active → ~50s active total, still under the 60s interval
        for (long t = 51_000_000; t <= 70_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        // Old (buggy) code added the 20s gap as one step on resume → fired. Fixed code must not.
        Check.That(fired).IsFalse();
    }

    [Fact]
    public void PauseGap_DoesNotLeakOntoActiveClock()
    {
        var scheduler = new EyeCareScheduler(Cfg);
        bool fired = false;
        scheduler.BreakDue += () => fired = true;

        for (long t = 0; t <= 30_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);
        // 20s paused (valid samples still flow while paused)
        for (long t = 31_000_000; t <= 50_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: true);
        for (long t = 51_000_000; t <= 70_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        Check.That(fired).IsFalse();
    }

    [Fact]
    public void Snooze_RePromptsAfterSnoozeMinutes_NotFullIntervalAgain()
    {
        var scheduler = new EyeCareScheduler(Cfg); // interval 60s, snooze 60s
        int fires = 0;
        scheduler.BreakDue += () => fires++;

        // Reach the interval → break #1.
        for (long t = 0; t <= 60_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);
        Check.That(fires).IsEqualTo(1);

        scheduler.Snooze();

        // Feed exactly SnoozeMinutes (60s) of active gaze → should re-prompt (#2).
        // Buggy code set the clock to -snooze, requiring interval+snooze (120s) → would NOT fire.
        for (long t = 61_000_000; t <= 121_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        Check.That(fires).IsEqualTo(2);
    }

    [Fact]
    public void DisabledGap_DoesNotCountAsActiveTime_OnReEnable()
    {
        var cfg = new EyeCareSettings
        {
            Enabled = true, IntervalMinutes = 1, NaturalBreakSeconds = 30, SnoozeMinutes = 1
        };
        var scheduler = new EyeCareScheduler(cfg);
        bool fired = false;
        scheduler.BreakDue += () => fired = true;

        for (long t = 0; t <= 30_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        cfg.Enabled = false;
        for (long t = 31_000_000; t <= 1_530_000_000; t += 60_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        cfg.Enabled = true;
        scheduler.Update(new GazePoint(500, 500, 1_531_000_000, IsValid: true), isPaused: false);

        Check.That(fired).IsFalse();
    }

    [Fact]
    public void BreakDue_HeldUntilInterruptible()
    {
        var scheduler = new EyeCareScheduler(Cfg);
        int fires = 0;
        scheduler.BreakDue += () => fires++;

        // Interval reached while a modal overlay is up (canInterrupt: false) → not shown yet,
        // but active time still accrues (the user is actively using the app).
        for (long t = 0; t <= 70_000_000; t += 1_000_000)
            scheduler.Update(new GazePoint(500, 500, t, IsValid: true), isPaused: false, canInterrupt: false);
        Check.That(fires).IsEqualTo(0);

        // Back to FreeLook → the pending break fires.
        scheduler.Update(new GazePoint(500, 500, 71_000_000, IsValid: true), isPaused: false, canInterrupt: true);
        Check.That(fires).IsEqualTo(1);
    }
}
