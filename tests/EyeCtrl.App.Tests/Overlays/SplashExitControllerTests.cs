// tests/EyeCtrl.App.Tests/Overlays/SplashExitControllerTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using Xunit;

/// <summary>
/// Pure decisions of the splash lifecycle (spec §6): the L16 catch-up rule (only shortens what
/// remains — r3 #1), beat transitions, exit-request acceptance windows (any live beat BEFORE the
/// tail; ignored once the tail has begun — r4 #5), and the dead-man/hard-deadline predicates.
/// No WPF. All durations in SECONDS of base course.
/// </summary>
public class SplashExitControllerTests
{
    [Theory]
    [InlineData(0.15, 0.15)]     // remaining below the floor: base pace, never slowed (r3 #1)
    [InlineData(0.34, 0.34)]     // the sub-0.35 band the r3 review called out
    [InlineData(0.99, 0.99)]     // between the bands: still base pace
    [InlineData(1.0, 1.0)]       // exactly the floor
    [InlineData(2.5, 1.0)]       // above the floor: compressed to it
    [InlineData(5.0, 1.0)]
    public void ComputeCatchUpDuration_OnlyShortensWhatRemains(double remainingSeconds, double expectedSeconds)
    {
        Check.That(SplashExitController.ComputeCatchUpDuration(TimeSpan.FromSeconds(remainingSeconds)))
            .IsEqualTo(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void ComputeCatchUpDuration_ZeroRemaining_StaysZero()
    {
        Check.That(SplashExitController.ComputeCatchUpDuration(TimeSpan.Zero)).IsEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void TryBeginExit_IsIdempotent_AndReportsDuration()
    {
        var controller = new SplashExitController();
        Check.That(controller.TryBeginExit(TimeSpan.FromSeconds(3), out var first)).IsTrue();
        Check.That(first).IsEqualTo(TimeSpan.FromSeconds(1));
        Check.That(controller.Beat).IsEqualTo(SplashBeat.CatchUp);

        Check.That(controller.TryBeginExit(TimeSpan.FromSeconds(3), out _)).IsFalse();
        Check.That(controller.Beat).IsEqualTo(SplashBeat.CatchUp);
    }

    [Theory]
    [InlineData(SplashBeat.Idle)]
    [InlineData(SplashBeat.Entrance)]
    [InlineData(SplashBeat.Fill)]
    [InlineData(SplashBeat.Filled)]
    public void TryBeginExit_IsAcceptedFromAnyBeatBeforeTheTail(SplashBeat beat)
    {
        var controller = new SplashExitController();
        controller.AdvanceTo(beat);
        Check.That(controller.TryBeginExit(TimeSpan.FromSeconds(2), out var d)).IsTrue();
        Check.That(d).IsEqualTo(TimeSpan.FromSeconds(1));
        Check.That(controller.Beat).IsEqualTo(SplashBeat.CatchUp);
    }

    [Theory]
    [InlineData(SplashBeat.CatchUp)]
    [InlineData(SplashBeat.Title)]
    [InlineData(SplashBeat.Hold)]
    [InlineData(SplashBeat.Exit)]
    [InlineData(SplashBeat.Closed)]
    public void TryBeginExit_IsRejectedOnceTheTailHasBegun(SplashBeat beat)
    {
        var controller = new SplashExitController();
        controller.AdvanceTo(beat);
        Check.That(controller.TryBeginExit(TimeSpan.FromSeconds(3), out _)).IsFalse();
        Check.That(controller.Beat).IsEqualTo(beat);          // untouched — the tail keeps running
    }

    [Fact]
    public void DeadMan_FiresOnlyPastFifteenSeconds_WhenNotClosed()
    {
        var controller = new SplashExitController();
        Check.That(controller.ShouldDeadManClose(TimeSpan.FromSeconds(14.9))).IsFalse();
        Check.That(controller.ShouldDeadManClose(TimeSpan.FromSeconds(15))).IsTrue();

        controller.MarkClosed();
        Check.That(controller.ShouldDeadManClose(TimeSpan.FromSeconds(60))).IsFalse();
    }

    /// <summary>The arming itself is glue-side (Task 5); the predicate here is stateless vs it.</summary>
    [Fact]
    public void HardDeadline_FiresOnlyPastThreeSeconds_WhenNotClosed()
    {
        var controller = new SplashExitController();
        Check.That(controller.ShouldHardClose(TimeSpan.FromSeconds(2.9))).IsFalse();
        Check.That(controller.ShouldHardClose(TimeSpan.FromSeconds(3))).IsTrue();

        controller.MarkClosed();
        Check.That(controller.ShouldHardClose(TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Fact]
    public void AdvanceTo_DoesNotReviveAClosedController()
    {
        var controller = new SplashExitController();
        controller.MarkClosed();
        controller.AdvanceTo(SplashBeat.Title);
        Check.That(controller.Beat).IsEqualTo(SplashBeat.Closed);
    }
}
