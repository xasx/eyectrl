namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The activation feedback, a pure consumer since ticket 3. Core's fixation rule owns the clock and
/// publishes both facts this VM needs: the per-sample dwell progress on the <see cref="GazeFrame"/> (the
/// shared <see cref="GazeCursorMovedMessage"/> feed) and <see cref="FixationActivatedMessage"/> when the
/// valid-time age completes. The VM keeps no DwellTarget, accumulates nothing, and computes no age — the
/// rule seam is the only ledger, which is what lets the replay harness measure the production behaviour.
/// <para>
/// The behaviour the old base class supplied is preserved or deliberately relocated:
/// </para>
/// <list type="bullet">
/// <item>progress resets to 0 on show and on hide (fresh state per fixation);</item>
/// <item>an activation is acted on once per shown fixation (<c>_completionSent</c>), so a duplicate or
/// late message cannot publish a second <see cref="ActivationCompletedMessage"/> — and a late one after
/// the button hid is ignored outright;</item>
/// <item>no activation while Paused / inside external dwell / while <c>GazeEngine.SuppressFixation</c> is
/// set is structural rather than local: the engine does not feed the detector in those states, and the
/// interaction state machine only accepts the completion in <c>Fixation</c>. The feedback is hidden on
/// pause anyway, and since ticket 4 also when a cooperating app takes over with its own dwell display —
/// see <see cref="Receive(ExternalDwellActiveMessage)"/>.</item>
/// </list>
/// The dwell-completion cue still publishes after the effect's message, matching the old
/// DwellOverlayViewModelBase ordering.
/// </summary>
public partial class ActivationButtonViewModel : MessengerViewModelBase,
    IRecipient<ShowActivationButtonMessage>,
    IRecipient<HideActivationButtonMessage>,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<FixationActivatedMessage>,
    IRecipient<ExternalDwellActiveMessage>
{
    [ObservableProperty] private PhysicalPoint _position = new(0, 0);
    [ObservableProperty] private double _dwellProgress;
    [ObservableProperty] private bool _isVisible;

    /// <summary>
    /// The rule's live fixation radius for the run that raised the show, in physical pixels. Carried
    /// on the show message (the adapter reads the detector at onset), so the halo marks the effective
    /// radius rather than the raw setting when adaptation has moved it.
    /// </summary>
    public double FixationRadiusPixels { get; private set; }

    // One completion per shown fixation. Reset on show; a message that arrives after hide is ignored,
    // which is what keeps "activation then immediate end" and "end then a late activation" from ever
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
        Position = message.Position;
        FixationRadiusPixels = message.FixationRadiusPixels;
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
        // centred halo would sit frozen on top of it — the one overlap ticket 4 newly introduced
        // (the old button was drawn beside it). Hide, exactly as the ring does. Only the normal show
        // path may bring the feedback back: the external app's own ending is not a fixation onset, so
        // nothing here may re-show it.
        if (!message.Active)
            return;
        IsVisible = false;
        DwellProgress = 0;
        _completionSent = false;
    }

    /// <inheritdoc />
    public void Receive(GazeCursorMovedMessage message)
    {
        // Progress comes from Core's valid-time ledger and is already 0 outside a fixation, so the VM
        // only forwards it while the button is up. Gap-held samples carry the last value; skip them.
        if (IsVisible && !message.IsInGap)
            DwellProgress = message.Frame.ActivationProgress;
    }

    /// <inheritdoc />
    public void Receive(FixationActivatedMessage message)
    {
        if (!IsVisible || _completionSent)
            return;
        _completionSent = true;
        DwellProgress = 1;
        // Effect first, then the cue — the same order the old detector-event path used (D8): the
        // activation must not be lost to a throwing cue subscriber.
        Messenger.Send(new ActivationCompletedMessage());
        Messenger.Send(new DwellActivationMessage());
    }
}
