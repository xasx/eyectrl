namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class CursorCircleViewModel : MessengerViewModelBase,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<StateChangedMessage>,
    IRecipient<ExternalDwellActiveMessage>,
    IRecipient<ShowActivationButtonMessage>,
    IRecipient<HideActivationButtonMessage>
{
    private readonly EyeCtrlSettings _settings;

    // Effective visibility = the interaction state allows a cursor AND no external-dwell target
    // is currently active. Two independent inputs are combined into the single IsVisible so there
    // is exactly one owner of the property (no writer races between state changes and external dwell).
    private bool _stateAllowsCursor = true;
    private bool _externalDwellActive;

    [ObservableProperty] private PhysicalPoint _position = new(0, 0);
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>
    /// True while the interaction state is Paused. Paused does not hide the ring: it stays visible
    /// (dimmed by the View) so the user can see that tracking is alive and where their gaze is.
    /// </summary>
    [ObservableProperty] private bool _isPaused;

    /// <summary>
    /// True from the activation widget's show until its hide — i.e. exactly while Core's rule reports a
    /// fixation the interaction state accepted. While pinned the ring does not follow gaze: it sits
    /// literally still on the onset centroid. That stillness is the "you're holding" signal the user
    /// asked for ("the circle almost never stands still").
    /// </summary>
    [ObservableProperty] private bool _isPinned;

    public double Size => _settings.Cursor.CircleSize;
    public string CircleColor => _settings.Cursor.CircleColor;
    public double Opacity => _settings.Cursor.CircleOpacity;
    public double Thickness => _settings.Cursor.CircleThickness;

    public CursorCircleViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger)
    {
        _settings = settings;
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        // Pinned means the ring stays where the fixation started; a gaze move inside the hold radius
        // must not nudge it. FreeLook (and Paused) follow as before, so the user can still see where
        // the tracker thinks they are looking.
        if (IsVisible && !IsPinned)
            Position = message.Position;
    }

    /// <summary>
    /// Pins the ring on the fixation's onset centroid. The message carries exactly what ticket 3's rule
    /// published at onset — <c>InteractionManager.FixationPoint</c> set from the detector's running
    /// centroid — NOT the latest filtered sample, which sits behind the saccade. Pinning on the latest
    /// sample would re-introduce the anchor offset the centroid rule removed.
    /// </summary>
    public void Receive(ShowActivationButtonMessage message)
    {
        Position = message.Position;
        IsPinned = true;
    }

    public void Receive(HideActivationButtonMessage message) => IsPinned = false;

    public void Receive(StateChangedMessage message)
    {
        // Paused keeps the ring (dimmed) — it is the user's only evidence that tracking is alive
        // while they are looking for the resume target. RepeatMode hides the normal circle: the
        // contracting repeat cursor replaces it there.
        IsPaused = message.NewState == InteractionState.Paused;
        // Paused is not a measured state — no sample feeds the rule — so it also releases the pin,
        // structurally rather than relying on the pause path's hide message arriving first.
        if (IsPaused)
            IsPinned = false;
        _stateAllowsCursor = message.NewState
            is InteractionState.FreeLook or InteractionState.Fixation or InteractionState.Paused;
        UpdateVisibility();
    }

    public void Receive(ExternalDwellActiveMessage message)
    {
        _externalDwellActive = message.Active;
        UpdateVisibility();
    }

    private void UpdateVisibility() => IsVisible = _stateAllowsCursor && !_externalDwellActive;
}
