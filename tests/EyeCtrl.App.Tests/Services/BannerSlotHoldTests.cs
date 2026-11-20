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

    [Fact]
    public void Hold_AfterAPreempt_LeavesTheAlertLive_ItsCloseWaits_ReleaseShows()
    {
        // S3: Hold() used to demote a LIVE pre-emptor into the queue, contradicting the documented
        // bypass — the caregiver alert is the safety channel and stays up through the guide's hold.
        var slot = new BannerSlot();
        var alert = new FakeBanner(); var waiting = new FakeBanner();

        slot.Preempt(alert);                       // the alert takes the slot while free
        Check.That(slot.Live).IsSameReferenceAs(alert);

        slot.Hold();                               // the hold must NOT demote the live alert
        Check.That(slot.Live).IsSameReferenceAs(alert);
        Check.That(alert.Hides).IsEqualTo(0);      // never hidden, never re-queued

        slot.Enqueue(waiting);                     // queued behind the hold; nothing shows
        Check.That(waiting.Shows).IsEqualTo(0);

        alert.Close();                             // the alert closes naturally during the hold
        Check.That(slot.Live).IsNull();            // marker cleared, dequeue still paused
        Check.That(waiting.Shows).IsEqualTo(0);    // the held banner waits

        slot.Release();                            // the hold lifts: the front of the queue shows
        Check.That(slot.Live).IsSameReferenceAs(waiting);
        Check.That(waiting.Shows).IsEqualTo(1);

        // And a pre-emptor arriving AFTER a hold still bypasses it (the existing pinned behaviour,
        // now also under the marker: the slot took it down → a later Hold would demote it).
        var second = new FakeBanner();
        slot.Preempt(second);
        Check.That(slot.Live).IsSameReferenceAs(second);
        slot.Withdraw(second);                     // slot-driven withdrawal also clears the marker
        Check.That(slot.Live).IsSameReferenceAs(waiting);
    }
}
