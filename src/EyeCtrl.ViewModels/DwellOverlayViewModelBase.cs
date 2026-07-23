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

    protected DwellDetector DwellDetector { get; }

    protected DwellOverlayViewModelBase(IMessenger messenger, int dwellCooldownMs,
        DwellContext context = DwellContext.Other, IStruggleTelemetrySink? sink = null)
        : base(messenger)
    {
        DwellDetector = new DwellDetector(dwellCooldownMs, context, sink);
        DwellDetector.TargetActivated += OnTargetActivated;
        DwellDetector.TargetProgress += OnTargetProgress;
    }

    protected abstract void OnTargetActivated(string targetId);
    protected abstract void OnTargetProgress(string targetId, double progress);

    public virtual void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            DwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    protected override void OnDispose()
    {
        DwellDetector.TargetActivated -= OnTargetActivated;
        DwellDetector.TargetProgress -= OnTargetProgress;
    }
}
