namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Base class for overlay ViewModels that own a single DwellDetector
/// and feed it gaze updates while visible. Derived classes add/clear targets on the protected
/// DwellDetector and react in OnTargetActivated / OnTargetProgress.
/// </summary>
public abstract partial class DwellOverlayViewModelBase : MessengerViewModelBase,
    IRecipient<GazeCursorMovedMessage>
{
    [ObservableProperty] private bool _isVisible;

    private readonly IDwellDetectorRegistry? _dwellRegistry;

    protected DwellDetector DwellDetector { get; }

    protected DwellOverlayViewModelBase(IMessenger messenger, int dwellCooldownMs,
        DwellContext context = DwellContext.Other, IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger)
    {
        DwellDetector = new DwellDetector(dwellCooldownMs, context, sink);
        DwellDetector.TargetActivated += OnDetectorTargetActivated;
        DwellDetector.TargetProgress += OnTargetProgress;
        _dwellRegistry = dwellRegistry;
        _dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            DwellDetector, context,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs));
    }

    protected abstract void OnTargetActivated(string targetId);
    protected abstract void OnTargetProgress(string targetId, double progress);

    /// <summary>
    /// Whether an activation on this surface publishes <see cref="Messages.DwellActivationMessage"/>
    /// (the dwell-completion cue's trigger). Default true — a new effectful surface is covered with
    /// no action. Override with false ONLY where OnTargetActivated is a no-op (no effect to
    /// confirm); a reflection pin in EyeCtrl.ViewModels.Tests forces new opt-outs to be conscious.
    /// </summary>
    protected virtual bool AnnounceDwellActivation => true;

    // D8 (spec): the effect runs FIRST — Messenger.Send is synchronous, so a throwing subscriber
    // must never be able to cost the user their activation — then the cue publishes.
    private void OnDetectorTargetActivated(string targetId)
    {
        OnTargetActivated(targetId);
        if (AnnounceDwellActivation)
            Messenger.Send(new DwellActivationMessage());
    }

    /// <summary>
    /// Whether this surface keeps accumulating dwell while the interaction state is Paused. The
    /// default is false — paused means input must not leave EyeCtrl: the keyboard types, quick
    /// actions send keystrokes and launch executives, the task switcher switches windows, all
    /// into whatever the OS foreground is. Override with <c>true</c> ONLY for surfaces whose
    /// dwell is the user's way OUT of a stuck state — the toolbar band (gaze-unpause), the modes
    /// menu (holds the pause toggle), the edge bar and the drag trail (release affordances) —
    /// and for overlays whose ONLY dismissal is a dwell (task switcher tiles, quick actions):
    /// those opt in and gate the EFFECT instead via <see cref="LastSampleWasPaused"/>.
    /// </summary>
    protected virtual bool DwellActiveWhilePaused => false;

    /// <summary>
    /// Whether the most recent received sample was paused. Set by <see cref="Receive"/> BEFORE
    /// the detector update, so an activation fired by that update observes its sample's pause
    /// state. Activation handlers on surfaces that opted into <see cref="DwellActiveWhilePaused"/>
    /// use this to suppress their OS-input effect while still performing their own dismissal —
    /// gating the dwell itself would make the dismissal unreachable (round-2 blocker: a paused
    /// task switcher with a dead close path is an inescapable fullscreen overlay).
    /// <see cref="Receive"/> OVERRIDES must stamp it via <see cref="NoteSamplePaused"/> themselves
    /// — the base only sets it on its own path, and an override that skips it leaves the flag
    /// stale-false and silently disarms the pause suppression above.
    /// </summary>
    protected bool LastSampleWasPaused { get; private set; }

    /// <summary>Stamps <see cref="LastSampleWasPaused"/> — the override-accessible writer.</summary>
    protected void NoteSamplePaused(bool isPaused) => LastSampleWasPaused = isPaused;

    /// <summary>
    /// The shared per-sample dwell gate: a gap-held sample never accumulates, and a paused
    /// sample accumulates only on surfaces that opted in via <see cref="DwellActiveWhilePaused"/>.
    /// Subclasses that override <see cref="Receive"/> MUST route through this — round-2 finding
    /// 2: three overrides reimplemented their own checks and silently bypassed the pause gate,
    /// leaving two of the opt-ins as dead code.
    /// </summary>
    protected bool SampleMayDwell(GazeCursorMovedMessage message) =>
        !message.IsInGap && (!message.IsPaused || DwellActiveWhilePaused);

    /// <summary>
    /// Feeds the sample to the dwell detector while visible. A gap-held sample is skipped entirely —
    /// its position is the frozen last valid one, so feeding it would accumulate dwell on whatever the
    /// user happened to be looking at when their eyes closed. Skipping, rather than resetting, leaves
    /// an in-progress dwell intact for when the eyes reopen. A paused sample is skipped for every
    /// surface that has not opted in via <see cref="DwellActiveWhilePaused"/> — same principle:
    /// input must not leave EyeCtrl while the user believes pause released it.
    /// </summary>
    public virtual void Receive(GazeCursorMovedMessage message)
    {
        LastSampleWasPaused = message.IsPaused;
        if (IsVisible && SampleMayDwell(message))
            DwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    protected override void OnDispose()
    {
        DwellDetector.TargetActivated -= OnDetectorTargetActivated;
        DwellDetector.TargetProgress -= OnTargetProgress;
        _dwellRegistry?.Unregister(this);
    }
}
