namespace EyeCtrl.App.Tests.Startup;

using EyeCtrl.App.Startup;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;

/// <summary>
/// The ring-arm look build's <c>--ring-arm=today|hybrid|run-only</c> launch argument (parsed beside
/// <c>--paused</c>). Not a setting: nothing persists, absent means today, and an unknown value runs
/// today (the App logs a warning and the startup line keeps the <c>--ring-arm</c> source so a silent
/// fallback is visible).
/// </summary>
public class RingArmArgumentTests
{
    [Fact]
    public void Absent_RunsToday_ByDefault()
    {
        var launch = RingArmArgument.Parse(new[] { "EyeCtrl.exe", "--paused" });

        Check.That(launch.Arm).IsEqualTo(RingArm.Today);
        Check.That(launch.Specified).IsFalse();
        Check.That(launch.UnknownValue).IsNull();
        Check.That(launch.PinPolicy).IsEqualTo(RingPinPolicy.OfferFollowing);
        Check.That(launch.HoldPolicy).IsEqualTo(RingHoldPolicy.None);
    }

    [Fact]
    public void TodayArgument_SetsOfferFollowing_AndNoHold()
    {
        var launch = RingArmArgument.Parse(new[] { "--ring-arm=today" });

        Check.That(launch.Arm).IsEqualTo(RingArm.Today);
        Check.That(launch.Specified).IsTrue();
        Check.That(launch.UnknownValue).IsNull();
        Check.That(launch.PinPolicy).IsEqualTo(RingPinPolicy.OfferFollowing);
        Check.That(launch.HoldPolicy).IsEqualTo(RingHoldPolicy.None);
    }

    [Fact]
    public void HybridArgument_SetsRunOnlyAndHybridFreeze()
    {
        // Arm B = (RunOnly, HybridFreeze 500/90/150).
        var launch = RingArmArgument.Parse(new[] { "--test-mode", "--ring-arm=hybrid" });

        Check.That(launch.Arm).IsEqualTo(RingArm.Hybrid);
        Check.That(launch.Specified).IsTrue();
        Check.That(launch.PinPolicy).IsEqualTo(RingPinPolicy.RunOnly);
        Check.That(launch.HoldPolicy).IsEqualTo(RingHoldPolicy.HybridFreeze);
    }

    [Fact]
    public void RunOnlyArgument_SetsRunOnly_AndNoHold()
    {
        // Arm C = (RunOnly, None).
        var launch = RingArmArgument.Parse(new[] { "--ring-arm=run-only" });

        Check.That(launch.Arm).IsEqualTo(RingArm.RunOnly);
        Check.That(launch.Specified).IsTrue();
        Check.That(launch.PinPolicy).IsEqualTo(RingPinPolicy.RunOnly);
        Check.That(launch.HoldPolicy).IsEqualTo(RingHoldPolicy.None);
    }

    [Fact]
    public void TheValue_IsCaseInsensitive_LikeTheOtherFlags()
    {
        var launch = RingArmArgument.Parse(new[] { "--RING-ARM=HyBrId" });

        Check.That(launch.Arm).IsEqualTo(RingArm.Hybrid);
    }

    [Fact]
    public void UnknownValue_RunsToday_AndNamesTheValue()
    {
        // Mutation: accept any value / crash on unknown → red.
        var launch = RingArmArgument.Parse(new[] { "--ring-arm=fast" });

        Check.That(launch.Arm).IsEqualTo(RingArm.Today);
        Check.That(launch.Specified).IsTrue();
        Check.That(launch.UnknownValue).IsEqualTo("fast");
        Check.That(launch.PinPolicy).IsEqualTo(RingPinPolicy.OfferFollowing);
        Check.That(launch.HoldPolicy).IsEqualTo(RingHoldPolicy.None);
    }
}
