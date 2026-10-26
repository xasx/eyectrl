namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// B4 fixup-01 H1: the bottom-centre slot can hold a drift notice for an unbounded time (a caregiver
/// alert stays up as long as its condition is sustained), so resuming it must RE-VALIDATE —
/// feedback not suppressed, the adaptation runtime still running, and the drift episode still
/// current. A notice that fails any of those is dropped, never popped late (§8.10, the disable edge,
/// a stale "recalibrate" hint). Ported from review 01's repro, driven through the REAL
/// <see cref="DriftNoticeService"/>, <see cref="SlotDriftNoticePresenter"/> and
/// <see cref="BannerSlot"/>; the drift window is prepared-invisible and only ever shown on the
/// positive control. The caregiver side is the reviewer's occupant (no window), driven by the real
/// <see cref="CaregiverAlertService"/>.
/// </summary>
[Collection("WpfApplication")]
public class DriftNoticeSlotRevalidationTests
{
    private sealed class Gate : IFeedbackGate
    {
        public bool IsFeedbackSuppressed { get; set; }
    }

    /// <summary>The reviewer's caregiver occupant, line for line with the production presenter.</summary>
    private sealed class CaregiverOccupant(IBannerSlot slot) : ICaregiverAlertPresenter, IBannerSlotOccupant
    {
        public bool Visible { get; private set; }

        public event EventHandler? Closed;

        public void ShowCaregiverAlert(CaregiverAlertCondition condition) => slot.Preempt(this);

        public void FadeOutCaregiverAlert()
        {
            if (slot.IsLive(this)) { Visible = false; Closed?.Invoke(this, EventArgs.Empty); } // fade completed
            else slot.Withdraw(this);
        }

        public void HideCaregiverAlert()
        {
            if (slot.IsLive(this)) { slot.Withdraw(this); return; }
            slot.Withdraw(this);
            HideFromSlot();
        }

        public void ShowInSlot() => Visible = true;

        public void HideFromSlot() { Visible = false; Closed?.Invoke(this, EventArgs.Empty); }

        public bool TryGetVisibleBounds(out PhysicalRect bounds) { bounds = new(1, 1, 10, 10); return Visible; }
    }

    private sealed class Clock
    {
        public long Now;
    }

    private sealed class Rig : IDisposable
    {
        public required BannerSlot Slot { get; init; }
        public required Gate Gate { get; init; }
        public required DriftNoticeWindow Window { get; init; }
        public required SlotDriftNoticePresenter Presenter { get; init; }
        public required DriftNoticeService Drift { get; init; }
        public required CaregiverAlertService Care { get; init; }
        public required CaregiverOccupant Caregiver { get; init; }
        public required Clock Clock { get; init; }

        public void Dispose()
        {
            Care.Dispose();
            Drift.Dispose();
            Window.Close();
        }
    }

    private static Rig Make()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        settings.Adaptation.CaregiverAlerts = true;

        var slot = new BannerSlot();
        var gate = new Gate();
        var window = InvisibleTestWindow.Prepare(new DriftNoticeWindow());
        var clock = new Clock();
        var caregiver = new CaregiverOccupant(slot);
        var care = new CaregiverAlertService(
            settings.Adaptation, gate, caregiver, nowMs: () => clock.Now);

        // The production wiring: the presenter's canShow reads the production service's live state.
        DriftNoticeService? drift = null;
        var presenter = new SlotDriftNoticePresenter(
            slot, window, canShow: () => drift?.CanShowNotice == true);
        drift = new DriftNoticeService(gate, presenter);

        return new Rig
        {
            Slot = slot, Gate = gate, Window = window, Presenter = presenter,
            Drift = drift, Care = care, Caregiver = caregiver, Clock = clock,
        };
    }

    private static StruggleAssessment Struggle() =>
        new(Array.Empty<FailureModeAssessment>(), 0.6);

    private static StruggleAssessment Drift() => new(
        new[] { new FailureModeAssessment(FailureMode.Drift, Score: 0.6, Confidence: 1.0, GatePassed: true, Context: null) },
        0.0);

    private static StruggleAssessment NoDrift() => new(
        new[] { new FailureModeAssessment(FailureMode.Drift, Score: 0.0, Confidence: 1.0, GatePassed: false, Context: null) },
        0.0);

    private static void Advance(Rig r, StruggleAssessment assessment, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            r.Clock.Now += 250;
            r.Care.OnAssessment(assessment);
        }
    }

    /// <summary>A caregiver alert goes up (60 s of sustained struggle), then a drift episode gates
    /// while feedback is NOT suppressed: the notice waits behind the alert.</summary>
    private static void AlertLiveWithDriftQueued(Rig r)
    {
        Advance(r, Struggle(), 300);
        Check.That(r.Caregiver.Visible).IsTrue();
        Check.That(r.Slot.IsLive(r.Presenter)).IsFalse();

        Check.That(r.Drift.OnAssessment(Drift())).IsTrue();
        Check.WithCustomMessage("the notice must wait behind the live alert")
            .That(r.Window.IsVisible).IsFalse();
        Check.That(r.Slot.IsLive(r.Presenter)).IsFalse();
    }

    // Review 01 repro 1: suppression hides the alert AT ONCE, and the slot then reaches the queued
    // notice while a password field has focus.
    [StaFact]
    public void SuppressionHidesTheAlert_AndTheQueuedDriftNoticeIsDropped()
    {
        using var r = Make();
        AlertLiveWithDriftQueued(r);

        r.Gate.IsFeedbackSuppressed = true; // the owner focuses a password field
        r.Clock.Now += 250;
        r.Care.OnAssessment(Struggle()); // §8.10: the alert goes down at once

        Check.That(r.Caregiver.Visible).IsFalse();
        Check.WithCustomMessage("§8.10: no drift notice may appear while feedback is suppressed")
            .That(r.Window.IsVisible).IsFalse();
        Check.WithCustomMessage("the dropped notice must not leave a phantom live occupant")
            .That(r.Slot.Live).IsNull();
    }

    // Review 01 repro 2: the alert's condition clears while feedback is suppressed, so its fade's
    // Closed advances the slot inside the sensitive context.
    [StaFact]
    public void TheAlertFadesWhileSuppressed_AndTheQueuedDriftNoticeIsDropped()
    {
        using var r = Make();
        AlertLiveWithDriftQueued(r);

        r.Gate.IsFeedbackSuppressed = true;
        r.Clock.Now += 250;
        r.Care.OnAssessment(StruggleAssessment.None); // the condition clears → fade → Closed → advance

        Check.WithCustomMessage("§8.10: no drift notice may appear while feedback is suppressed")
            .That(r.Window.IsVisible).IsFalse();
        Check.That(r.Slot.Live).IsNull();
    }

    // Review 01 repro 3: adaptation is switched off. AdaptationActivation.StopRuntime disposes the
    // drift service first (withdrawing its notice), then the caregiver service (which hides the
    // alert and would otherwise advance the slot onto the queued notice).
    [StaFact]
    public void DisablingAdaptation_NeverPopsTheQueuedDriftNotice()
    {
        using var r = Make();
        AlertLiveWithDriftQueued(r);

        r.Drift.Dispose();
        r.Care.Dispose();

        Check.WithCustomMessage("adaptation is off: no drift notice may come up after the disable")
            .That(r.Window.IsVisible).IsFalse();
        Check.That(r.Slot.Live).IsNull();
    }

    // The coordinator's 4th case: the drift episode ends while the notice waits behind the alert.
    [StaFact]
    public void ADriftEpisodeThatEndedWhileQueued_IsDropped()
    {
        using var r = Make();
        AlertLiveWithDriftQueued(r);

        r.Clock.Now += 250;
        r.Drift.OnAssessment(NoDrift()); // the episode ends while the notice waits

        r.Care.OnAssessment(StruggleAssessment.None); // the alert fades → the slot reaches the notice

        Check.WithCustomMessage("a recalibrate hint for an ended episode is stale noise — drop it")
            .That(r.Window.IsVisible).IsFalse();
        Check.That(r.Slot.Live).IsNull();
    }

    // The positive control: a queued notice with NOTHING invalidated still resumes. Without this
    // every drop assertion above would pass vacuously.
    [StaFact]
    public void AQueuedNoticeResumes_WhenNothingInvalidatedIt()
    {
        using var r = Make();
        AlertLiveWithDriftQueued(r);

        r.Clock.Now += 250;
        r.Care.OnAssessment(StruggleAssessment.None); // the alert clears normally (no suppression)

        Check.That(r.Caregiver.Visible).IsFalse();
        Check.WithCustomMessage("the queued notice must resume when its episode is still current")
            .That(r.Window.IsVisible).IsTrue();
        Check.That(r.Slot.IsLive(r.Presenter)).IsTrue();
    }

    // DriftNoticeService.Dispose withdraws a LIVE notice too (not only a queued one): switching
    // adaptation off must take the banner down rather than leave it for its 8 s hold.
    [StaFact]
    public void DisposingTheService_HidesALiveDriftNotice()
    {
        using var r = Make();

        Check.That(r.Drift.OnAssessment(Drift())).IsTrue();
        Check.That(r.Window.IsVisible).IsTrue(); // the slot was free

        r.Drift.Dispose();

        Check.WithCustomMessage("teardown must take a live notice down")
            .That(r.Window.IsVisible).IsFalse();
        Check.That(r.Slot.Live).IsNull();
    }
}
