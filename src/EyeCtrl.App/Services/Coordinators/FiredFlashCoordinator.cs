namespace EyeCtrl.App.Services.Coordinators;

using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The "fired" flash (Wave C ticket C1): when a dwell fires, the cursor ring makes one brief
/// luminance step; when the fire is an accepted activation offer that opens the magnifier, the
/// magnifier's rim flashes instead. Gated by <c>DwellSettings.RingFiredFlashEnabled</c>.
/// <para>
/// Driven from the App layer on purpose: the ring team's hard constraint is that the flash never
/// changes the ring VM's hold, pin, keep, position or visibility, so this coordinator holds no
/// <see cref="CursorCircleViewModel"/> state and only calls display methods on the two windows.
/// </para>
/// <list type="bullet">
/// <item><b>Which fires:</b> exactly those that announce themselves with
/// <see cref="DwellActivationMessage"/> — the dwell-completion cue's trigger. Keyboard keys and the
/// prediction bar (the typing family) never announce (DwellSurfaceCensusTests classifies them), so
/// keys are excluded by the same rule that keeps the cue sound off them; repeat-mode clicks do not
/// announce either.</item>
/// <item><b>Hidden ring:</b> the window drops a flash while it is not on screen (its own
/// visibility is the truth: settings, scroll mode, calibration, the action menu and the magnifier
/// all hide it without the VM). Nothing is queued.</item>
/// <item><b>Offer accept:</b> <see cref="ActivationButtonViewModel"/> sends
/// <see cref="ActivationCompletedMessage"/> and then, in the same call, its
/// <see cref="DwellActivationMessage"/>. The state change that hides the ring is posted, so the
/// ring is still on screen for that second message; it would flash and vanish in the same
/// instant. The completion therefore marks the very next announcement as the accept's own, which
/// is skipped; the magnifier's rim carries the confirmation instead.</item>
/// <item><b>Magnifier:</b> flashes when it becomes visible for a FRESH entry
/// (<see cref="ManualSelectionOverlayViewModel.Recapture"/>). Core raises a fresh entry only from an
/// accepted offer (<c>InteractionManager.CompleteActivation</c>); Back from the action menu restores
/// the view without a recapture and does not flash.</item>
/// </list>
/// </summary>
internal sealed class FiredFlashCoordinator : CoordinatorBase
{
    // Set by an offer's completion, consumed by the announcement that follows it in the same call.
    private bool _offerAcceptAnnounced;

    protected override void OnInitialize()
    {
        var context = Context!;
        context.Messenger.Register<FiredFlashCoordinator, ActivationCompletedMessage>(
            this, static (r, _) => r._offerAcceptAnnounced = true);
        context.Messenger.Register<FiredFlashCoordinator, DwellActivationMessage>(
            this, static (r, _) => r.OnDwellFired());
        context.ManualSelectionVM.PropertyChanged += OnManualSelectionVMPropertyChanged;
    }

    private void OnDwellFired()
    {
        var context = Context;
        if (context is null) return;

        if (_offerAcceptAnnounced)
        {
            _offerAcceptAnnounced = false;
            return;
        }

        if (!context.Settings.Dwell.RingFiredFlashEnabled) return;

        // Every announcing surface fires on the UI thread today; a future off-thread sender is
        // marshalled rather than allowed to touch the window from its own thread. The window's
        // hidden check then runs at execution time, so a ring hidden in between still drops it.
        var dispatcher = context.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            context.OverlayManager.CursorCircle.FlashFired();
        else
            dispatcher.BeginInvoke(() => context.OverlayManager.CursorCircle.FlashFired());
    }

    private void OnManualSelectionVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var context = Context;
        if (context is null
            || e.PropertyName != nameof(ManualSelectionOverlayViewModel.IsVisible)
            || !context.ManualSelectionVM.IsVisible
            || !context.ManualSelectionVM.Recapture
            || !context.Settings.Dwell.RingFiredFlashEnabled) return;

        context.OverlayManager.ManualSelectionOverlay.FlashRim();
    }

    protected override void OnDispose()
    {
        if (Context is null) return;
        Context.ManualSelectionVM.PropertyChanged -= OnManualSelectionVMPropertyChanged;
    }
}
