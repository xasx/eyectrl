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
    /// True while the interaction state is Paused. Paused does not hide the ring: it stays visible
    /// (dimmed by the View) so the user can see that tracking is alive and where their gaze is.
    /// </summary>
    [ObservableProperty] private bool _isPaused;

    /// <summary>
    /// True from Core's accepted settle (the <c>PinRing</c> event) until the fixation ends. While pinned
    /// the ring does not follow gaze: it eases to the settle centroid and then sits literally still
    /// there. That stillness is the "you're holding" signal the user asked for ("the circle almost
    /// never stands still"). Deliberately NOT keyed on the activation button's show/hide: the button's
    /// lifetime is its own (it still shows at onset in ticket 7, and will linger in ticket 8).
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
        // must not nudge it. FreeLook (and Paused) follow as before, so the user can still see where
        // the tracker thinks they are looking.
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
        // Paused keeps the ring (dimmed) — it is the user's only evidence that tracking is alive
        // while they are looking for the resume target. RepeatMode hides the normal circle: the
        // contracting repeat cursor replaces it there.
        IsPaused = message.NewState == InteractionState.Paused;
        // Belt and braces for the release: a pinned ring implies Fixation, so any other state cancels
        // the pin (and an in-flight glide) structurally, even if the release message is ever lost.
        // This is also the Paused rule that predates the pin.
        if (message.NewState != InteractionState.Fixation)
        {
            _gliding = false;
            IsPinned = false;
            Position = _latestGaze;
        }
        _stateAllowsCursor = message.NewState
            is InteractionState.FreeLook or InteractionState.Fixation or InteractionState.Paused;
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

    private void UpdateVisibility() => IsVisible = _stateAllowsCursor && !_externalDwellActive;
}
