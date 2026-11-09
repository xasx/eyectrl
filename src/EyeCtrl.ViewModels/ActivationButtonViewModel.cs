namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
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
    /// What the face names, locked in when the offer is shown (B2). <see cref="SelectionMode.ManualSelection"/>
    /// opens the magnifier, every other mode the action menu. Read by the window through the binding
    /// coordinator, so a mode change while the offer is up leaves this offer's face as it was.
    /// </summary>
    [ObservableProperty] private ActivationButtonContent _content;

    /// <summary>
    /// True while the button's fill is held in the engine's pause grace: the gaze has left the
    /// button and the progress has stopped rising, so the window draws the fill dimmed (B2, port of
    /// R2's <c>CursorCircleViewModel.IsFillPaused</c>). Entered after
    /// <see cref="FillPausedStillFrames"/> valid still frames outside the hit area, left after
    /// <see cref="FillPausedReleaseFrames"/> consecutive credited frames, and cleared at either end
    /// of the fill and by every reset listed on <see cref="DwellProgress"/>. Derived from the frames:
    /// the engine's progress only stands still (with the offer alive) when the sample in grace was
    /// not credited.
    /// </summary>
    [ObservableProperty] private bool _isFillPaused;

    /// <summary>Valid frames the fill must stand still before the paused look shows. Two frames
    /// (60 ms at 30 ms) debounces a single out-of-area sample, while the engine's grace is 500 ms;
    /// the fill is dimmed for the rest of the excursion.</summary>
    public const int FillPausedStillFrames = 2;

    /// <summary>Consecutive credited frames that end the paused look (fixup-01 F3). Symmetric with
    /// the entry rule: one rising frame cannot clear it, so a gaze dithering across the hit area —
    /// out, out, in, out, … credits a single sample per return — cannot strobe the dim at ~11 Hz.</summary>
    public const int FillPausedReleaseFrames = 2;

    private readonly EyeCtrlSettings _settings;
    private double _lastFillProgress;
    private int _fillStillFrames;
    private int _fillRisingFrames;

    /// <summary>
    /// The offer's hit radius in physical pixels, carried on the show message. The drawn disc is
    /// deliberately smaller than it: the window draws a fixed 100 DIP face, so no drawn feedback ever
    /// lies outside the area the dwell is counted within, and the hit area stays the offer engine's.
    /// No longer the fixation radius: since ticket 8 the feedback is on the button's dwell, and the
    /// ring owns the hold's marking.
    /// </summary>
    public double HitRadiusPixels { get; private set; }

    // One completion per shown offer. Reset on show; a message that arrives after hide is ignored,
    // which is what keeps "fire then immediate close" and "close then a late fire" from ever
    // producing two completions or a completion for a button that is down.
    private bool _completionSent;

    /// <summary>Initializes a new instance of the <see cref="ActivationButtonViewModel"/> class.
    /// Used by tests that do not care which face the button draws; the production container resolves
    /// the settings-taking overload.</summary>
    /// <param name="messenger">The messenger the overlay message stream rides.</param>
    public ActivationButtonViewModel(IMessenger messenger)
        : this(messenger, new EyeCtrlSettings())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ActivationButtonViewModel"/> class.</summary>
    /// <param name="messenger">The messenger the overlay message stream rides.</param>
    /// <param name="settings">The live settings; <see cref="EyeCtrlSettings.SelectionMode"/> picks the
    /// button's face at show time.</param>
    public ActivationButtonViewModel(IMessenger messenger, EyeCtrlSettings settings)
        : base(messenger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <inheritdoc />
    public void Receive(ShowActivationButtonMessage message)
    {
        ButtonCenter = message.ButtonCenter;
        HitRadiusPixels = message.HitRadiusPx;
        Content = _settings.SelectionMode == SelectionMode.ManualSelection
            ? ActivationButtonContent.Magnifier
            : ActivationButtonContent.Menu;
        ResetFillPause(0);
        _completionSent = false;
        IsVisible = true;
    }

    /// <inheritdoc />
    public void Receive(HideActivationButtonMessage message)
    {
        IsVisible = false;
        ResetFillPause(0);
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
        ResetFillPause(0);
        _completionSent = false;
    }

    /// <inheritdoc />
    public void Receive(GazeCursorMovedMessage message)
    {
        // Progress comes from Core's button-dwell ledger and is already 0 outside an offer, so the VM
        // only forwards it while the button is up. Gap-held samples carry the last value; skip them.
        if (!IsVisible || message.IsInGap)
            return;

        double progress = message.Frame.ButtonDwellProgress;
        if (!double.IsFinite(progress))
            progress = 0.0;
        progress = Math.Clamp(progress, 0.0, 1.0);
        DwellProgress = progress;
        AdvanceFillPause(message.Position, progress);
    }

    /// <summary>
    /// Derives the paused look from the frames (B2). The engine credits only samples inside the
    /// button's hit area, so with the offer still alive a progress that has not risen over
    /// <see cref="FillPausedStillFrames"/> valid frames, while the gaze is outside the hit area, is
    /// exactly the pause grace. A single out-of-area sample never dims the fill, and the release needs
    /// <see cref="FillPausedReleaseFrames"/> consecutive credited frames so a gaze dithering across
    /// the area cannot strobe the dim.
    /// </summary>
    /// <param name="gaze">The sample's filtered gaze position, in physical pixels.</param>
    /// <param name="progress">The frame's button-dwell progress, already clamped to 0–1.</param>
    private void AdvanceFillPause(PhysicalPoint gaze, double progress)
    {
        // Neither end of the fill is "paused": 0 is nothing drawn, 1 is the fired wait for the
        // accept/decline (the engine reports 1.0 while it waits).
        if (progress <= 0 || progress >= 1)
        {
            ResetFillPause(progress);
            return;
        }

        if (progress > _lastFillProgress)
        {
            _fillStillFrames = 0;
            _fillRisingFrames++;
        }
        else
        {
            _fillRisingFrames = 0;
            _fillStillFrames++;
        }

        _lastFillProgress = progress;

        bool outsideHitArea = gaze.DistanceTo(ButtonCenter) > HitRadiusPixels;
        if (IsFillPaused)
        {
            if (_fillRisingFrames >= FillPausedReleaseFrames)
                IsFillPaused = false;
        }
        else if (outsideHitArea && _fillStillFrames >= FillPausedStillFrames)
        {
            IsFillPaused = true;
        }
    }

    /// <summary>Clears the paused look and its frame bookkeeping for the next show.</summary>
    private void ResetFillPause(double progress)
    {
        IsFillPaused = false;
        DwellProgress = progress;
        _lastFillProgress = progress;
        _fillStillFrames = 0;
        _fillRisingFrames = 0;
    }

    /// <inheritdoc />
    public void Receive(ActivationOfferFiredMessage message)
    {
        // The offer's dwell completed. Hand the snapshot to Core (effect) first, then the cue — the
        // same order the old detector-event path used (D8): the activation must not be lost to a
        // throwing cue subscriber.
        //
        // Dropping a hidden fire strands the engine's fired lock until a teardown, so every path that
        // hides the button with a live offer must also clear it in Core. Today there is exactly one —
        // ExternalDwellActiveMessage below, paired with the coordinator's clear request — and the
        // adapter ships no other hide path while an offer is live (review B F6).
        if (!IsVisible || _completionSent)
            return;
        _completionSent = true;
        ResetFillPause(1);
        Messenger.Send(new ActivationCompletedMessage(message.Offer));
        Messenger.Send(new DwellActivationMessage());
    }
}
