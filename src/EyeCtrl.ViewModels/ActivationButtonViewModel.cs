namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The activation feedback, a pure consumer. Since ticket 8 its lifetime follows the activation
/// OFFER: Core's engine publishes the button's show, movement and hide (<see cref="ShowActivationButtonMessage"/>/
/// <see cref="HideActivationButtonMessage"/>), the per-sample button-dwell progress on the
/// <see cref="GazeFrame"/> (the shared <see cref="GazeCursorMovedMessage"/> feed), and
/// <see cref="ActivationOfferFiredMessage"/> when the dwell completes. The VM keeps no DwellTarget,
/// accumulates nothing, and computes no age.
/// <para>
/// The behaviour the old base class supplied is preserved or deliberately relocated:
/// </para>
/// <list type="bullet">
/// <item>progress resets to 0 on show and on hide (fresh state per offer);</item>
/// <item>a completion is acted on once per shown offer (<c>_completionSent</c>), so a duplicate or
/// late message cannot publish a second <see cref="ActivationCompletedMessage"/> — and a late one after
/// the button hid is ignored outright;</item>
/// <item>the age signal (<see cref="FixationActivatedMessage"/>) no longer completes anything: it is
/// replay-only since ticket 8. Resting at a point can never activate.</item>
/// <item>no activation while Paused / inside external dwell / while <c>GazeEngine.SuppressFixation</c> is
/// set is structural rather than local: the engine clears the offer on those teardowns, and the
/// interaction state machine only accepts the completion in Fixation or FreeLook.</item>
/// </list>
/// The dwell-completion cue still publishes after the effect's message, matching the old
/// DwellOverlayViewModelBase ordering.
/// </summary>
public partial class ActivationButtonViewModel : MessengerViewModelBase,
    IRecipient<ShowActivationButtonMessage>,
    IRecipient<HideActivationButtonMessage>,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<ActivationOfferFiredMessage>,
    IRecipient<ExternalDwellActiveMessage>
{
    /// <summary>B, the offer button's centre in physical screen pixels. The window is centred here
    /// with no clamping — the window manager read-back must equal this value.</summary>
    [ObservableProperty] private PhysicalPoint _buttonCenter = new(0, 0);
    [ObservableProperty] private double _dwellProgress;
    [ObservableProperty] private bool _isVisible;

    /// <summary>
    /// The offer's hit radius in physical pixels, carried on the show message. The halo is sized from
    /// it — outer radius at or inside <see cref="HitRadiusPixels"/> — so no drawn feedback ever lies
    /// outside the area the dwell is counted within. No longer the fixation radius: since ticket 8 the
    /// feedback is on the button's dwell, and the ring owns the hold's marking.
    /// </summary>
    public double HitRadiusPixels { get; private set; }

    // One completion per shown offer. Reset on show; a message that arrives after hide is ignored,
    // which is what keeps "fire then immediate close" and "close then a late fire" from ever
    // producing two completions or a completion for a button that is down.
    private bool _completionSent;

    /// <summary>Initializes a new instance of the <see cref="ActivationButtonViewModel"/> class.</summary>
    /// <param name="messenger">The messenger the overlay message stream rides.</param>
    public ActivationButtonViewModel(IMessenger messenger)
        : base(messenger)
    {
    }

    /// <inheritdoc />
    public void Receive(ShowActivationButtonMessage message)
    {
        ButtonCenter = message.ButtonCenter;
        HitRadiusPixels = message.HitRadiusPx;
        DwellProgress = 0;
        _completionSent = false;
        IsVisible = true;
    }

    /// <inheritdoc />
    public void Receive(HideActivationButtonMessage message)
    {
        IsVisible = false;
        DwellProgress = 0;
        _completionSent = false;
    }

    /// <inheritdoc />
    public void Receive(ExternalDwellActiveMessage message)
    {
        // A cooperating app draws its own dwell feedback at the point it just took over, and the
        // button would sit frozen on top of it. Hide, exactly as the ring does. Only the normal show
        // path may bring the feedback back: the external app's own ending is not an offer, so nothing
        // here may re-show it. Core's own offer is cleared by the same takeover on the gaze thread.
        if (!message.Active)
            return;
        IsVisible = false;
        DwellProgress = 0;
        _completionSent = false;
    }

    /// <inheritdoc />
    public void Receive(GazeCursorMovedMessage message)
    {
        // Progress comes from Core's button-dwell ledger and is already 0 outside an offer, so the VM
        // only forwards it while the button is up. Gap-held samples carry the last value; skip them.
        if (IsVisible && !message.IsInGap)
            DwellProgress = message.Frame.ButtonDwellProgress;
    }

    /// <inheritdoc />
    public void Receive(ActivationOfferFiredMessage message)
    {
        // The offer's dwell completed. Hand the snapshot to Core (effect) first, then the cue — the
        // same order the old detector-event path used (D8): the activation must not be lost to a
        // throwing cue subscriber.
        if (!IsVisible || _completionSent)
            return;
        _completionSent = true;
        DwellProgress = 1;
        Messenger.Send(new ActivationCompletedMessage(message.Offer));
        Messenger.Send(new DwellActivationMessage());
    }
}
