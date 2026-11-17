// tests/EyeCtrl.App.Tests/Services/BannerSlotHoldTests.cs
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using NFluent;
using Xunit;

public class BannerSlotHoldTests
{
    private sealed class FakeBanner : IBannerSlotOccupant
    {
        public event EventHandler? Closed;
        public int Shows; public int Hides;
        public void ShowInSlot() => Shows++;
        public void HideFromSlot() => Hides++;
        public bool TryGetVisibleBounds(out Core.Models.PhysicalRect bounds) { bounds = default; return false; }
        public IReadOnlyList<Core.Models.PhysicalRect> LiveBoundsOrEmpty() => Array.Empty<Core.Models.PhysicalRect>();
        public void ReassertForOrder() { }
        public void Close() => Closed?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public void Hold_HidesAndRequeuesLive_AtTheFront_ReleaseResumesWithoutDrops()
    {
        var slot = new BannerSlot();
        var a = new FakeBanner(); var b = new FakeBanner();
        slot.Enqueue(a);
        Check.That(slot.Live).IsSameReferenceAs(a);

        slot.Hold();
        Check.That(a.Hides).IsEqualTo(1);
        Check.That(slot.Live).IsNull();

        slot.Enqueue(b);                       // queued, not shown during the hold
        Check.That(b.Shows).IsEqualTo(0);

        slot.Release();
        Check.That(slot.Live).IsSameReferenceAs(a);   // front of the queue: resumes first
        Check.That(b.Shows).IsEqualTo(0);             // still waiting its turn — nothing dropped
    }

    [Fact]
    public void Preempt_BypassesTheHold_CaregiverAlertsAreTheSafetyChannel()
    {
        var slot = new BannerSlot();
        var live = new FakeBanner(); var alert = new FakeBanner();
        slot.Enqueue(live);
        slot.Hold();
        Check.That(slot.Live).IsNull();

        slot.Preempt(alert);                   // pinned behaviour (r1 F24): the alert TAKES the slot

        Check.That(slot.Live).IsSameReferenceAs(alert);
        Check.That(alert.Shows).IsEqualTo(1);
        Check.That(live.Shows).IsEqualTo(1);   // re-queued at the front; resumes after the alert
        slot.Release();                        // the hold release does not disturb the live alert
        Check.That(slot.Live).IsSameReferenceAs(alert);
    }

    [Fact]
    public void Hold_WithAnEmptySlot_QueuesWithoutShowing()
    {
        var slot = new BannerSlot();
        var a = new FakeBanner();
        slot.Hold();
        slot.Enqueue(a);
        Check.That(slot.Live).IsNull();
        slot.Release();
        Check.That(slot.Live).IsSameReferenceAs(a);
    }
}
