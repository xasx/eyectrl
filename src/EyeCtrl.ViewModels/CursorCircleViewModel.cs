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
    IRecipient<PinRingMessage>,
    IRecipient<ReleaseRingMessage>
{
    private readonly EyeCtrlSettings _settings;

    // Effective visibility = the interaction state allows a cursor AND no external-dwell target
    // is currently active. Two independent inputs are combined into the single IsVisible so there
    // is exactly one owner of the property (no writer races between state changes and external dwell).
    private bool _stateAllowsCursor = true;
    private bool _externalDwellActive;

    // The latest sample position, tracked even while pinned: the release snaps the ring back to gaze
    // immediately, and while pinned the gaze channel deliberately does not move Position.
    private PhysicalPoint _latestGaze;

    // The last state the machine reported. A pin is only accepted in Fixation, which makes "a pinned
    // ring implies Fixation" structural: an exit posted by the dispatcher can land between the gaze
    // thread's state check and its pin post (review A, F4), and without this the ring would stay
    // frozen in FreeLook or Paused — with no later release on the keyboard path.
    private InteractionState _lastState = InteractionState.FreeLook;

    // The glide (ticket 7): the ring eases from where it was to the settle centroid over
    // RingGlide.DurationMs, driven by the sample clock (the frames the gaze channel already delivers),
    // never by a WPF animation. _glideStartUs is -1 until the first frame after the pin, so the glide
    // is measured from the first sample at or after the pin edge.
    private bool _gliding;
    private PhysicalPoint _glideFrom;
    private PhysicalPoint _glideTo;
    private long _glideStartUs = -1;

    /// <summary>One call to the overlay: move the ring to <see cref="Target"/>, and whether this write
    /// is a glide frame (instrument 2 skips glide frames — their cadence is the animation's, not the
    /// ring's). The record carries both at once so the window never sees a position before it knows it
    /// is a pin (critique M3/N10).</summary>
    public readonly record struct RingMove(PhysicalPoint Target, bool Glide);

    [ObservableProperty] private PhysicalPoint _position = new(0, 0);
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>
    /// The one position channel for pin edges and glide frames, forwarded by the binding coordinator to
    /// <c>ICursorCircleOverlay.MoveTo</c>. <see cref="Position"/> stays the channel for ordinary
    /// gaze-following writes and its existing readers; during a glide only this property moves, so the
    /// glide's frames never reach instrument 2 through the Position path.
    /// </summary>
    [ObservableProperty] private RingMove _moveTo;

    /// <summary>
    /// True while the interaction state is Paused. Paused hides the ring (<see cref="_stateAllowsCursor"/>
    /// excludes it). IsPaused survives purely as a defensive View input: no production path shows the
    /// window while paused (the startup show precedes the paused-launch toggle, whose single state
    /// message dims and then hides in one dispatcher callback, so the dim is never presented) — but a
    /// future direct-show path must still read as paused rather than as the quiet FreeLook ring.
    /// </summary>
    [ObservableProperty] private bool _isPaused;

    /// <summary>
    /// True from Core's <c>PinRing</c> (the accepted settle, or an offer appearing) until Core's
    /// <c>ReleaseRing</c>: the fixation's end, or — while an activation offer is alive — the offer's
    /// close, so the ring does not move while the user looks at the button. While pinned the ring does
    /// not follow gaze: it eases to the pin and then sits literally still there. That stillness is the
    /// "you're holding" signal the user asked for ("the circle almost never stands still"). The VM
    /// itself is keyed on Core's two messages, never on the button's show/hide.
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
        _latestGaze = message.Position;

        if (_gliding)
        {
            AdvanceGlide(message.Frame.TimestampMicroseconds);
            return;
        }

        // Pinned means the ring stays where the fixation settled; a gaze move inside the hold radius
        // must not nudge it. Hidden states (Paused included) do not follow either: the IsVisible
        // gate below stops Position updates while the window is away, so the ring reappears
        // exactly where UpdateVisibility's reappear snap put it.
        if (IsVisible && !IsPinned)
            Position = message.Position;
    }

    /// <summary>
    /// Pins the ring on the settle centroid Core published on the accepted settle. The glide starts
    /// from the ring's current position — the point it was following a moment ago — and ends exactly on
    /// the pin; the first frame after the pin starts the clock.
    /// </summary>
    public void Receive(PinRingMessage message)
    {
        // Only a fixation may pin. The check is against the last state the machine REPORTED, so a
        // release/exit that arrives before this message (F4's race) refuses the pin outright.
        if (_lastState != InteractionState.Fixation)
            return;

        _glideFrom = Position;
        _glideTo = message.Position;
        _glideStartUs = -1;
        _gliding = true;
        IsPinned = true;
    }

    /// <summary>
    /// Releases the pin and snaps to gaze — the next drawn position is the latest sample, applied
    /// directly, with no glide sweeping behind the eye after it has already landed (decision 2). Also
    /// cancels a glide that was still in flight, so nothing keeps interpolating toward a stale pin.
    /// </summary>
    public void Receive(ReleaseRingMessage message)
    {
        _gliding = false;
        IsPinned = false;
        Position = _latestGaze;
    }

    public void Receive(StateChangedMessage message)
    {
        _lastState = message.NewState;
        // Paused hides the ring: the PauseIndicator strip is the pause feedback, and resume needs no
        // cursor — the past-edge summon carries no pause gate and the toolbar band is deliberately
        // dwellable while paused (ToolbarViewModel.DwellActiveWhilePaused). The dim (IsPaused below)
        // remains only as the View's defensive look; see IsPaused's summary.
        // RepeatMode hides the normal circle too: the contracting repeat cursor replaces it there.
        IsPaused = message.NewState == InteractionState.Paused;
        // Belt and braces for the release: a pinned ring implies Fixation or FreeLook — FreeLook only
        // because the ring stays at the offer's P while the user looks at the button, and Core's
        // ReleaseRing (offer closed, or the run ended with no offer) is what frees it there. Any
        // other state cancels the pin (and an in-flight glide) structurally, even if the release
        // message is ever lost. This is also the Paused rule that predates the pin.
        if (message.NewState is not (InteractionState.Fixation or InteractionState.FreeLook))
        {
            _gliding = false;
            IsPinned = false;
            Position = _latestGaze;
        }
        _stateAllowsCursor = message.NewState
            is InteractionState.FreeLook or InteractionState.Fixation;
        UpdateVisibility();
    }

    public void Receive(ExternalDwellActiveMessage message)
    {
        _externalDwellActive = message.Active;
        UpdateVisibility();
    }

    private void AdvanceGlide(long timestampUs)
    {
        if (_glideStartUs < 0)
        {
            // The first sample at or after the pin: it starts the clock and draws the glide's start.
            _glideStartUs = timestampUs;
            MoveTo = new RingMove(_glideFrom, Glide: true);
            return;
        }

        double elapsedMs = (timestampUs - _glideStartUs) / 1000.0;
        if (elapsedMs >= RingGlide.DurationMs)
        {
            // Landed: the ordinary Position channel draws the exact pin (and is instrumented).
            _gliding = false;
            Position = _glideTo;
            return;
        }

        MoveTo = new RingMove(RingGlide.At(_glideFrom, _glideTo, elapsedMs), Glide: true);
    }

    private void UpdateVisibility()
    {
        var wasVisible = IsVisible;
        var nowVisible = _stateAllowsCursor && !_externalDwellActive;

        // A ring that reappears after a hidden stretch draws where the gaze is NOW. The hidden
        // window keeps its last drawn Left/Top, so without this the first shown frame presents
        // the position from before the stretch — minutes old when the stretch was a pause. The
        // pin is exempt: a pinned ring must stay exactly where it is until Core releases it.
        // Position is written BEFORE IsVisible so the coordinator's Show lands on a window that
        // is already at the live gaze — same order as the activation halo (position, then show),
        // and for the same reason: the other order presents the previous position for a frame.
        if (!wasVisible && nowVisible && !IsPinned)
            Position = _latestGaze;

        IsVisible = nowVisible;
    }
}
