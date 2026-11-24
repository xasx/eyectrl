namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services.Coordinators;
using NFluent;
using Xunit;

public class ModalInteractionStateTests
{
    [Fact]
    public void GuideOpen_RaisesChanged_OncePerRealFlip()
    {
        var modal = new ModalInteractionState();
        var flips = new List<bool>();
        modal.GuideOpenChanged += flips.Add;

        modal.GuideOpen = true;
        modal.GuideOpen = true;   // no change: no event
        modal.GuideOpen = false;

        Check.That(flips).HasSize(2);
        Check.That(flips[0]).IsTrue();
        Check.That(flips[1]).IsFalse();
    }

    [Fact]
    public void GuideOpen_AThrowingSubscriber_DoesNotStopTheOthers()
    {
        var modal = new ModalInteractionState();
        var seen = new List<bool>();
        modal.GuideOpenChanged += _ => throw new InvalidOperationException("boom");
        modal.GuideOpenChanged += seen.Add;

        modal.GuideOpen = true;

        Check.That(seen).HasSize(1);
    }

    [Fact]
    public void BackstageOpen_RaisesChanged_OncePerRealFlip()
    {
        var modal = new ModalInteractionState();
        var flips = new List<bool>();
        modal.BackstageOpenChanged += flips.Add;

        modal.BackstageOpen = true;
        modal.BackstageOpen = true;   // no change: no event
        modal.BackstageOpen = false;

        Check.That(flips).HasSize(2);
        Check.That(flips[0]).IsTrue();
        Check.That(flips[1]).IsFalse();
    }

    [Fact]
    public void BackstageOpen_AThrowingSubscriber_DoesNotStopTheOthers()
    {
        // Acceptance criterion: a gate whose recompute throws must not leave the remaining peers
        // holding a stale half-armed state while Backstage owns the screen.
        var modal = new ModalInteractionState();
        var seen = new List<bool>();
        modal.BackstageOpenChanged += _ => throw new InvalidOperationException("boom");
        modal.BackstageOpenChanged += seen.Add;

        modal.BackstageOpen = true;

        Check.That(seen).HasSize(1);
    }

    [Fact]
    public void GuideCalibrationActive_RaisesChanged_OncePerRealFlip()
    {
        // D3 (PR-review n1): the guide's calibration flag raises like GuideOpen — the
        // activation-offer coordinator recomputes the "guide" reason on each flip, so a missed
        // edge would leave offers suspended (or live) through the whole calibration window.
        var modal = new ModalInteractionState();
        var flips = new List<bool>();
        modal.GuideCalibrationActiveChanged += flips.Add;

        modal.GuideCalibrationActive = true;
        modal.GuideCalibrationActive = true;   // no change: no event (the per-target re-sends)
        modal.GuideCalibrationActive = false;

        Check.That(flips).HasSize(2);
        Check.That(flips[0]).IsTrue();
        Check.That(flips[1]).IsFalse();
    }

    [Fact]
    public void GuideCalibrationActive_AThrowingSubscriber_DoesNotStopTheOthers()
    {
        var modal = new ModalInteractionState();
        var seen = new List<bool>();
        modal.GuideCalibrationActiveChanged += _ => throw new InvalidOperationException("boom");
        modal.GuideCalibrationActiveChanged += seen.Add;

        modal.GuideCalibrationActive = true;

        Check.That(seen).HasSize(1);
    }
}
