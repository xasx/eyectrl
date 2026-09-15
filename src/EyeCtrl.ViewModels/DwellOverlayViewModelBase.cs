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
        DwellDetector.TargetActivated += OnTargetActivated;
        DwellDetector.TargetProgress += OnTargetProgress;
        _dwellRegistry = dwellRegistry;
        _dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            DwellDetector, context,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs));
    }

    protected abstract void OnTargetActivated(string targetId);
    protected abstract void OnTargetProgress(string targetId, double progress);

    /// <summary>
    /// Whether this surface keeps accumulating dwell while the interaction state is Paused. The
    /// default is false — paused means input must not leave EyeCtrl: the keyboard types, quick
    /// actions send keystrokes and launch executives, the task switcher switches windows, all
    /// into whatever the OS foreground is. Override with <c>true</c> ONLY for the safety surfaces
    /// whose dwell is the user's way OUT of a stuck state: the toolbar band (gaze-unpause), the
    /// modes menu (holds the pause toggle), the edge bar and the drag trail (release affordances).
    /// </summary>
    protected virtual bool DwellActiveWhilePaused => false;

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
        if (IsVisible && !message.IsInGap && (!message.IsPaused || DwellActiveWhilePaused))
            DwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    protected override void OnDispose()
    {
        DwellDetector.TargetActivated -= OnTargetActivated;
        DwellDetector.TargetProgress -= OnTargetProgress;
        _dwellRegistry?.Unregister(this);
    }
}
