namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ActivationButtonViewModel : DwellOverlayViewModelBase,
    IRecipient<ShowActivationButtonMessage>,
    IRecipient<HideActivationButtonMessage>
{
    private const string TargetId = "activate";

    private readonly EyeCtrlSettings _settings;

    [ObservableProperty] private PhysicalPoint _position = new(0, 0);
    [ObservableProperty] private double _dwellProgress;

    public ActivationButtonViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink? sink = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.ActivationButton, sink)
    {
        _settings = settings;
    }

    public void Receive(ShowActivationButtonMessage message)
    {
        Position = message.Position;
        IsVisible = true;
        DwellProgress = 0;
        DwellDetector.ClearTargets();
        DwellDetector.AddTarget(new DwellTarget(TargetId,
            message.Position,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.ActivationButtonDwellMs));
    }

    public void Receive(HideActivationButtonMessage message)
    {
        IsVisible = false;
        DwellProgress = 0;
        DwellDetector.ClearTargets();
    }

    protected override void OnTargetActivated(string targetId) =>
        Messenger.Send(new ActivationCompletedMessage());

    protected override void OnTargetProgress(string targetId, double progress) =>
        DwellProgress = progress;
}
