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
            // The occupant contract (B4 fixup-01 M2): a hide raises Closed. Without this the slot's
            // pre-emption re-entrancy defence is never exercised.
            Closed?.Invoke(this, EventArgs.Empty);
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

    // B4 fixup-01 M2 (kills the K1 ordering mutant): with THREE occupants — drift live, a status
    // card queued, then a caregiver pre-empt — the displaced drift's Closed must not advance the
    // queue. If Preempt hid the drift before becoming live, the card would be shown and then
    // overwritten by the caregiver: two banners up, one tracked.
    [Fact]
    public void APreempt_WithABannerQueued_ShowsThePreemptor_AndNeverTheQueuedCardFirst()
    {
        var slot = new BannerSlot();
        var drift = new RecordingBanner("drift");
        var card = new RecordingBanner("card");
        var caregiver = new RecordingBanner("caregiver");

        slot.Enqueue(drift);     // live
        slot.Enqueue(card);      // waiting
        slot.Preempt(caregiver);

        Check.WithCustomMessage("the caregiver takes the slot")
            .That(slot.Live).IsSameReferenceAs(caregiver);
        Check.WithCustomMessage(
                "the queued card must not appear while the caregiver is live: the displaced banner's "
                + "Closed must not advance the queue")
            .That(card.Shows).IsEqualTo(0);
        Check.That(card.Visible).IsFalse();

        caregiver.Close();
        Check.WithCustomMessage("the displaced drift notice resumes at the FRONT of the queue")
            .That(slot.Live).IsSameReferenceAs(drift);
        Check.That(drift.Shows).IsEqualTo(2);

        drift.Close();
        Check.That(slot.Live).IsSameReferenceAs(card);
    }

    // B4 fixup-01 L1: a banner that cannot show must not leave the slot stalled with an invisible
    // live occupant — drop it and continue with the next queued banner.
    [Fact]
    public void AThrowingShowInSlot_DropsThatBanner_AndTheNextQueuedBannerShows()
    {
        var slot = new BannerSlot();
        var blocker = new RecordingBanner("blocker");
        var faulty = new FaultyBanner("faulty") { ThrowOnShow = true };
        var next = new RecordingBanner("next");

        slot.Enqueue(blocker);
        slot.Enqueue(faulty);
        slot.Enqueue(next);

        Check.ThatCode(blocker.Close).DoesNotThrow();

        Check.WithCustomMessage("the throwing banner must not stay live")
            .That(slot.Live).IsSameReferenceAs(next);
        Check.WithCustomMessage("the next queued banner must show")
            .That(next.Visible).IsTrue();
        Check.That(next.Shows).IsEqualTo(1);
    }

    // B4 fixup-01 L1: a faulting displaced hide must never stop the pre-emptor (the safety channel)
    // from taking the slot.
    [Fact]
    public void AThrowingDisplacedHide_DoesNotStopThePreemptor()
    {
        var slot = new BannerSlot();
        var faulty = new FaultyBanner("faulty") { ThrowOnHide = true };
        var caregiver = new RecordingBanner("caregiver");

        slot.Enqueue(faulty);
        Check.ThatCode(() => slot.Preempt(caregiver)).DoesNotThrow();

        Check.WithCustomMessage("the caregiver alert takes the slot even though the hide threw")
            .That(slot.Live).IsSameReferenceAs(caregiver);
        Check.That(caregiver.Visible).IsTrue();
        Check.That(faulty.Hides).IsEqualTo(1);

        caregiver.Close();
        Check.WithCustomMessage("the displaced banner still returns to the front of the queue")
            .That(slot.Live).IsSameReferenceAs(faulty);
    }

    /// <summary>A banner that can be told to throw from ShowInSlot / HideFromSlot.</summary>
    private sealed class FaultyBanner(string name) : IBannerSlotOccupant
    {
        public string Name { get; } = name;
        public bool ThrowOnShow { get; init; }
        public bool ThrowOnHide { get; init; }
        public int Shows { get; private set; }
        public int Hides { get; private set; }
        public bool Visible { get; private set; }

        public event EventHandler? Closed;

        public void ShowInSlot()
        {
            Shows++;
            if (ThrowOnShow)
                throw new InvalidOperationException($"{Name} cannot show");
            Visible = true;
        }

        public void HideFromSlot()
        {
            Hides++;
            if (ThrowOnHide)
                throw new InvalidOperationException($"{Name} cannot hide");
            Visible = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        public bool TryGetVisibleBounds(out PhysicalRect bounds)
        {
            bounds = new PhysicalRect(0, 0, 10, 10);
            return Visible;
        }
    }
}
