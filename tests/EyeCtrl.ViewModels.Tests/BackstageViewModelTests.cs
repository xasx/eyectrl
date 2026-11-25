namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The headless half of the Backstage menu (Ticket 02). The safety-critical rows are:
/// the pause opt-in (the owner must be able to quit/restart from inside a pause), the
/// single-detector modal swap (no background card can be dwelt through a confirmation), and the
/// leave-before-arm latch (an opening gaze, or the modal button that just dismissed a modal, cannot
/// fire the card underneath it).
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class BackstageViewModelTests
{
    private static readonly PhysicalPoint CardCentre = new(620, 580);
    private static readonly PhysicalRect CardRect = new(500, 500, 240, 160);
    private static readonly PhysicalPoint Away = new(10, 10);
    private static readonly PhysicalPoint CloseCentre = new(1800, 100);
    private static readonly PhysicalPoint ModalChoiceCentre = new(1500, 700);

    private static BackstageViewModel NewVm(WeakReferenceMessenger messenger, EyeCtrlSettings? settings = null) =>
        new(messenger, settings ?? new EyeCtrlSettings());

    private static void ShowWithCardAndClose(BackstageViewModel vm)
    {
        vm.ShowOverlay();
        vm.UpdateCardPosition(BackstageViewModel.QuitCardId, CardRect);
        vm.UpdateCloseButtonPosition(CloseCentre);
    }

    /// <summary>Sends the one sample outside every target that arms the latch.</summary>
    private static void Arm(WeakReferenceMessenger messenger, long timestampUs = 0) =>
        messenger.Send(new GazeCursorMovedMessage(Away, timestampUs));

    private static void Dwell(WeakReferenceMessenger messenger, PhysicalPoint at, long fromUs, long throughUs,
        bool paused = false) =>
        DwellDrive.Hold(fromUs, throughUs,
            t => messenger.Send(new GazeCursorMovedMessage(at, t) { IsPaused = paused }));

    [Fact]
    public void PausedSamples_ActivateACard_BecauseBackstageIsTheEscape()
    {
        // The owner controls this PC only by gaze and can get stuck in pause. Backstage opts into
        // dwell-while-paused so Quit/Restart stay reachable; a card must therefore fire from paused
        // samples, not only from unpaused ones.
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var vm = NewVm(messenger, settings);
        ShowWithCardAndClose(vm);
        Arm(messenger);

        var fired = new List<string>();
        vm.CardActivated += fired.Add;

        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1_000L;
        Dwell(messenger, CardCentre, 30_000, 30_000 + dwellUs, paused: true);

        Check.That(vm.DwellActiveWhilePausedForTest).IsTrue();
        Check.That(fired).ContainsExactly(BackstageViewModel.QuitCardId);
    }

    [Fact]
    public void ModalSwap_BackgroundCard_CannotDwellThroughTheModal()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var vm = NewVm(messenger, settings);
        ShowWithCardAndClose(vm);
        Arm(messenger);

        var cards = new List<string>();
        var choices = new List<string>();
        vm.CardActivated += cards.Add;
        vm.ModalChoiceActivated += choices.Add;

        // The gaze starts its dwell on the Quit card, then Quit's confirmation opens. The card's
        // 120 ms of accumulated dwell is still live at that instant.
        Dwell(messenger, CardCentre, 30_000, 150_000);
        vm.ShowModal("quit");
        vm.RegisterModalChoice("modal_cancel");
        vm.UpdateModalChoiceCentre("modal_cancel", ModalChoiceCentre);

        // ONLY the modal's own choice is registered. The card and the Close disc are gone.
        Check.That(vm.DetectorForTest.Targets.Select(t => t.Id))
            .ContainsExactly("modal_cancel");
        Check.That(vm.IsModalOpen).IsTrue();

        // A full dwell on where the card was must do nothing at all.
        Dwell(messenger, CardCentre, 200_000, 200_000 + (settings.Dwell.ToolbarButtonDwellMs * 1_000L));
        Check.That(cards).IsEmpty();
        Check.That(choices).IsEmpty();

        // The modal's own button is live (and was armed while the gaze rested on the card).
        Dwell(messenger, ModalChoiceCentre, 2_000_000,
            2_000_000 + (settings.Dwell.ToolbarButtonDwellMs * 1_000L));
        Check.That(choices).ContainsExactly("modal_cancel");
    }

    [Fact]
    public void Latch_TargetRegisteredUnderTheRestingGaze_DoesNotFireUntilTheGazeLeaves()
    {
        // The opening gaze (the toolbar Backstage disc) must not complete the first card it lands
        // on. Registration holds the target; only a sample outside its release area arms it.
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var vm = NewVm(messenger, settings);
        ShowWithCardAndClose(vm);

        var fired = new List<string>();
        vm.CardActivated += fired.Add;
        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1_000L;

        // No Arm() yet: the gaze is already on the card when it is registered.
        Dwell(messenger, CardCentre, 0, dwellUs + 30_000);
        Check.That(fired).IsEmpty();

        Arm(messenger, dwellUs + 60_000);
        Dwell(messenger, CardCentre, dwellUs + 90_000, (2 * dwellUs) + 120_000);
        Check.That(fired).ContainsExactly(BackstageViewModel.QuitCardId);
    }

    [Fact]
    public void ReLayoutOfALiveCard_KeepsItsDwell()
    {
        // A DPI change or a theme rebuild re-reports the same face. That must rebase the target,
        // never re-register and re-hold it: a held target under the resting gaze would silently
        // stall a dwell the owner had already started.
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var vm = NewVm(messenger, settings);
        ShowWithCardAndClose(vm);
        Arm(messenger);

        var fired = new List<string>();
        vm.CardActivated += fired.Add;
        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1_000L;

        Dwell(messenger, CardCentre, 30_000, 150_000);
        vm.UpdateCardPosition(BackstageViewModel.QuitCardId, CardRect);
        Dwell(messenger, CardCentre, 180_000, 180_000 + dwellUs);

        Check.That(fired).ContainsExactly(BackstageViewModel.QuitCardId);
    }

    [Fact]
    public void CardActivation_ClearsEveryTarget_BeforeDispatching()
    {
        // The Settings/guide precedent: clear before the handler runs, so a re-entrant look or a
        // target left under the gaze cannot fire the same act twice.
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var vm = NewVm(messenger, settings);
        ShowWithCardAndClose(vm);
        Arm(messenger);

        var targetsSeenByHandler = -1;
        vm.CardActivated += _ => targetsSeenByHandler = vm.DetectorForTest.Targets.Count;

        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1_000L;
        Dwell(messenger, CardCentre, 30_000, 30_000 + dwellUs);

        Check.That(targetsSeenByHandler).IsEqualTo(0);
    }

    [Fact]
    public void CloseDwell_ReportsProgressAndPaused_AndResetsOnEveryClear()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var vm = NewVm(messenger, settings);
        ShowWithCardAndClose(vm);
        Arm(messenger);

        // Progress routes to the round disc's own observable.
        Dwell(messenger, CloseCentre, 30_000, 180_000);
        Check.That(vm.CloseDwellProgress).IsStrictlyGreaterThan(0);
        Check.That(vm.CloseDwellProgress).IsStrictlyLessThan(1);

        var fired = 0;
        vm.CloseRequested += () => fired++;
        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1_000L;
        Dwell(messenger, CloseCentre, 300_000, 300_000 + dwellUs);
        Check.That(fired).IsEqualTo(1);
        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.CloseDwellProgress).IsEqualTo(0);
        Check.That(vm.CloseDwellPaused).IsFalse();

        // Every (re)open and modal transition starts the disc from zero.
        vm.ShowOverlay();
        vm.CloseDwellProgress = 0.5;
        vm.CloseDwellPaused = true;
        vm.ShowModal("quit");
        Check.That(vm.CloseDwellProgress).IsEqualTo(0);
        Check.That(vm.CloseDwellPaused).IsFalse();

        vm.CloseDwellProgress = 0.5;
        vm.CloseDwellPaused = true;
        vm.DismissModal();
        Check.That(vm.CloseDwellProgress).IsEqualTo(0);
        Check.That(vm.CloseDwellPaused).IsFalse();

        vm.ShowOverlay();
        vm.CloseDwellProgress = 0.5;
        vm.CloseDwellPaused = true;
        vm.HideOverlay();
        Check.That(vm.CloseDwellProgress).IsEqualTo(0);
        Check.That(vm.CloseDwellPaused).IsFalse();
    }

    [Fact]
    public void CloseTargetPauseChanged_RoutesToCloseDwellPaused()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        settings.Dwell.DwellPauseGraceMs = 350;
        using var vm = NewVm(messenger, settings);
        vm.ShowOverlay();
        vm.UpdateCloseButtonPosition(CloseCentre);
        Arm(messenger);

        Dwell(messenger, CloseCentre, 30_000, 300_000);
        messenger.Send(new GazeCursorMovedMessage(Away, 330_000));
        Check.That(vm.CloseDwellPaused).IsTrue();

        messenger.Send(new GazeCursorMovedMessage(CloseCentre, 360_000));
        Check.That(vm.CloseDwellPaused).IsFalse();
    }

    [Fact]
    public void HiddenSamples_NeverAccumulate_EvenWithTargetsRegistered()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var vm = NewVm(messenger, settings);
        ShowWithCardAndClose(vm);
        Arm(messenger);

        var fired = new List<string>();
        vm.CardActivated += fired.Add;
        vm.IsVisible = false;   // the base's visibility gate must survive this class's override

        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1_000L;
        Dwell(messenger, CardCentre, 30_000, 30_000 + dwellUs);
        Check.That(fired).IsEmpty();
    }

    [Fact]
    public void GapHeldSamples_NeverAccumulate()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        using var vm = NewVm(messenger, settings);
        ShowWithCardAndClose(vm);
        Arm(messenger);

        var fired = new List<string>();
        vm.CardActivated += fired.Add;

        // A blink streams the frozen last position; it must not complete a dwell.
        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1_000L;
        DwellDrive.Hold(30_000, 30_000 + dwellUs,
            t => messenger.Send(new GazeCursorMovedMessage(
                new GazeFrame(CardCentre, t, IsInGap: true))));

        Check.That(fired).IsEmpty();
    }

    [Fact]
    public void ShowOverlay_ClearsThePreviousSessionsLayout()
    {
        // The VM is a DI singleton. A card from the last session must not be resurrected by a
        // RestoreMainSurface() after the next open, before that open's layout ran.
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        vm.ShowOverlay();
        vm.UpdateCardPosition(BackstageViewModel.QuitCardId, CardRect);
        vm.IsVisible = false;   // hidden without the explicit HideOverlay clear

        vm.ShowOverlay();
        vm.RestoreMainSurface();

        Check.That(vm.DetectorForTest.Targets).IsEmpty();
    }

    [Fact]
    public void StatusStrip_SetStatusAndPauseBadge_AreAppliedWithoutTouchingDwellState()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        vm.SetStatus("Version 1.4.2.0", "Installed · Standard", "Supervision off");
        vm.ShowOverlay();

        Check.That(vm.VersionText).IsEqualTo("Version 1.4.2.0");
        Check.That(vm.InstallText).IsEqualTo("Installed · Standard");
        Check.That(vm.SupervisorText).IsEqualTo("Supervision off");

        messenger.Send(new GazeCursorMovedMessage(Away, 0) { IsPaused = true });
        Check.That(vm.PausedIndicatorVisible).IsTrue();

        messenger.Send(new GazeCursorMovedMessage(Away, 30_000) { IsPaused = false });
        Check.That(vm.PausedIndicatorVisible).IsFalse();

        messenger.Send(new GazeCursorMovedMessage(Away, 60_000) { IsPaused = true });
        vm.HideOverlay();
        Check.That(vm.PausedIndicatorVisible).IsFalse();

        // The open kept the texts; hide must not blank them either (the next open pushes fresh ones).
        Check.That(vm.VersionText).IsEqualTo("Version 1.4.2.0");
        Check.That(vm.InstallText).IsEqualTo("Installed · Standard");
        Check.That(vm.SupervisorText).IsEqualTo("Supervision off");
    }

    [Fact]
    public void RemovingACard_DropsItFromTheRememberedLayoutAndTheDetector()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = NewVm(messenger);
        ShowWithCardAndClose(vm);

        vm.RemoveCard(BackstageViewModel.QuitCardId);
        Check.That(vm.DetectorForTest.Targets.Select(t => t.Id))
            .ContainsExactly(BackstageViewModel.CloseTargetId);

        // A modal dismiss restores only what is still remembered.
        vm.ShowModal("quit");
        vm.DismissModal();
        Check.That(vm.DetectorForTest.Targets.Select(t => t.Id))
            .ContainsExactly(BackstageViewModel.CloseTargetId);
    }
}
