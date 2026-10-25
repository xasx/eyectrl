namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;

/// <summary>
/// B4's bottom-centre banner slot: ONE banner at a time, occupants queue FIFO, and a caregiver
/// alert pre-empts — the displaced banner returns to the FRONT of the queue, not the back. The
/// tests use recording occupants, so no window is constructed or shown; the real presenters'
/// routing and the windows' Hidden events are pinned in <see cref="BannerSlotWiringTests"/>.
/// </summary>
public class BannerSlotTests
{
    /// <summary>A banner that records how the slot treated it and closes on command.</summary>
    private sealed class RecordingBanner(string name) : IBannerSlotOccupant
    {
        public string Name { get; } = name;
        public int Shows { get; private set; }
        public int Hides { get; private set; }
        public bool Visible { get; private set; }
        public PhysicalRect Bounds { get; set; } = new(100, 200, 560, 72);

        public event EventHandler? Closed;

        public void ShowInSlot()
        {
            Shows++;
            Visible = true;
        }

        public void HideFromSlot()
        {
            Hides++;
            Visible = false;
        }

        public bool TryGetVisibleBounds(out PhysicalRect bounds)
        {
            bounds = Bounds;
            return Visible;
        }

        public void Close()
        {
            Visible = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        public override string ToString() => Name;
    }

    private static void AssertShown(RecordingBanner banner) =>
        Check.WithCustomMessage($"'{banner.Name}' must be on screen").That(banner.Visible).IsTrue();

    [Fact]
    public void TwoQueuedBanners_ShowOneAfterTheOther_AndNeverOverlap()
    {
        var slot = new BannerSlot();
        var first = new RecordingBanner("drift");
        var second = new RecordingBanner("card");

        slot.Enqueue(first);
        slot.Enqueue(second);

        Check.WithCustomMessage("only the first banner may take the slot")
            .That(slot.Live).IsSameReferenceAs(first);
        AssertShown(first);
        Check.WithCustomMessage("a queued banner must not be shown while the slot is busy")
            .That(second.Shows).IsEqualTo(0);
        Check.WithCustomMessage("the slot's bounds are the LIVE occupant's")
            .That(slot.TryGetVisibleBounds(out var liveBounds)).IsTrue();
        Check.That(liveBounds).IsEqualTo(first.Bounds);

        first.Close();
        Check.WithCustomMessage("the next banner shows when the live one closes")
            .That(slot.Live).IsSameReferenceAs(second);
        AssertShown(second);
        Check.That(first.Visible).IsFalse();

        second.Close();
        Check.That(slot.Live).IsNull();
        Check.That(slot.TryGetVisibleBounds(out _)).IsFalse();
        Check.That(slot.LiveBoundsOrEmpty()).IsEmpty();
    }

    [Fact]
    public void ACaregiverAlert_Preempts_TheLiveBanner_AndTheDisplacedBannerComesBackAfter()
    {
        var slot = new BannerSlot();
        var drift = new RecordingBanner("drift");
        var caregiver = new RecordingBanner("caregiver");

        slot.Enqueue(drift);
        slot.Preempt(caregiver);

        Check.WithCustomMessage("the caregiver alert takes the slot at once")
            .That(slot.Live).IsSameReferenceAs(caregiver);
        Check.That(caregiver.Shows).IsEqualTo(1);
        Check.WithCustomMessage("the displaced banner is taken down, not left underneath")
            .That(drift.Hides).IsEqualTo(1);
        Check.That(drift.Visible).IsFalse();

        caregiver.Close();

        Check.WithCustomMessage("the pre-empted banner comes back after the alert")
            .That(slot.Live).IsSameReferenceAs(drift);
        AssertShown(drift);
        Check.That(drift.Shows).IsEqualTo(2);
    }

    [Fact]
    public void APreemptedBanner_ReturnsToTheFrontOfTheQueue_NotTheBack()
    {
        var slot = new BannerSlot();
        var drift = new RecordingBanner("drift");
        var card = new RecordingBanner("card");
        var caregiver = new RecordingBanner("caregiver");

        slot.Enqueue(drift);   // live
        slot.Enqueue(card);    // waiting
        slot.Preempt(caregiver); // displaces drift after card was already queued

        caregiver.Close();
        Check.WithCustomMessage("the pre-empted banner resumes BEFORE banners that were already waiting")
            .That(slot.Live).IsSameReferenceAs(drift);

        drift.Close();
        Check.That(slot.Live).IsSameReferenceAs(card);
    }

    [Fact]
    public void TheSameBanner_IsNeverQueuedTwice()
    {
        var slot = new BannerSlot();
        var drift = new RecordingBanner("drift");
        var card = new RecordingBanner("card");

        slot.Enqueue(drift);
        slot.Enqueue(card);
        slot.Enqueue(card); // a duplicate request, not a second showing

        drift.Close();
        Check.That(slot.Live).IsSameReferenceAs(card);
        card.Close();
        Check.WithCustomMessage("the duplicate must not occupy a second turn")
            .That(slot.Live).IsNull();
    }

    [Fact]
    public void EnqueueingTheLiveBanner_RefreshesIt_InsteadOfQueueingASecondCopy()
    {
        var slot = new BannerSlot();
        var drift = new RecordingBanner("drift");

        slot.Enqueue(drift);
        slot.Enqueue(drift);

        Check.WithCustomMessage("the live banner is refreshed in place")
            .That(drift.Shows).IsEqualTo(2);
        Check.That(slot.Live).IsSameReferenceAs(drift);

        drift.Close();
        Check.WithCustomMessage("no phantom copy may be left behind")
            .That(slot.Live).IsNull();
    }

    [Fact]
    public void PreemptingTheLiveBanner_RefreshesIt_ForANewerCondition()
    {
        var slot = new BannerSlot();
        var caregiver = new RecordingBanner("caregiver");

        slot.Preempt(caregiver);
        slot.Preempt(caregiver); // a newer onset on the one alert window

        Check.That(caregiver.Shows).IsEqualTo(2);
        Check.That(slot.Live).IsSameReferenceAs(caregiver);

        caregiver.Close();
        Check.That(slot.Live).IsNull();
    }

    [Fact]
    public void AStaleCloseFromADisplacedBanner_DoesNotAdvanceTheSlot()
    {
        var slot = new BannerSlot();
        var drift = new RecordingBanner("drift");
        var caregiver = new RecordingBanner("caregiver");
        var card = new RecordingBanner("card");

        slot.Enqueue(drift);
        slot.Enqueue(card);
        slot.Preempt(caregiver);

        drift.Close(); // e.g. a late timer on the window that was already taken down
        Check.WithCustomMessage("only the LIVE occupant's close may advance the queue")
            .That(slot.Live).IsSameReferenceAs(caregiver);
        Check.That(card.Shows).IsEqualTo(0);
    }

    [Fact]
    public void Withdraw_TakesTheLiveBannerDown_WithoutReturningItToTheQueue()
    {
        var slot = new BannerSlot();
        var drift = new RecordingBanner("drift");
        var card = new RecordingBanner("card");

        slot.Enqueue(drift);
        slot.Enqueue(card);
        slot.Withdraw(drift);

        Check.That(drift.Hides).IsEqualTo(1);
        Check.WithCustomMessage("the withdrawn banner must be gone for good; the next one shows")
            .That(slot.Live).IsSameReferenceAs(card);

        card.Close();
        Check.That(slot.Live).IsNull();
    }

    [Fact]
    public void Withdraw_OfAQueuedBanner_DropsItFromTheQueue()
    {
        var slot = new BannerSlot();
        var drift = new RecordingBanner("drift");
        var card = new RecordingBanner("card");

        slot.Enqueue(drift);
        slot.Enqueue(card);
        slot.Withdraw(card);
        Check.That(card.Shows).IsEqualTo(0);

        drift.Close();
        Check.That(slot.Live).IsNull();
    }
}
