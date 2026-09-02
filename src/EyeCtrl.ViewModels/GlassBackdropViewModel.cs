namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class GlassBackdropViewModel : DwellOverlayViewModelBase,
    IRecipient<ShowBackdropMessage>,
    IRecipient<HideBackdropMessage>
{
    private const string HideTargetId = "backdrop_hide";

    private readonly EyeCtrlSettings _settings;

    [ObservableProperty] private PhysicalPoint _hideButtonCenter = new(0, 0);
    [ObservableProperty] private double _hideButtonProgress;

    public GlassBackdropViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.Backdrop, sink, dwellRegistry)
    {
        _settings = settings;
    }

    public void Receive(ShowBackdropMessage message)
    {
        HideButtonCenter = message.HideButtonCenter;
        HideButtonProgress = 0;
        DwellDetector.ClearTargets();
        DwellDetector.AddTarget(new DwellTarget(
            HideTargetId,
            message.HideButtonCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.BackdropHideButtonDwellMs));
        IsVisible = true;
    }

    public void Receive(HideBackdropMessage message)
    {
        IsVisible = false;
        HideButtonProgress = 0;
        DwellDetector.ClearTargets();
    }

    protected override void OnTargetActivated(string targetId)
    {
        if (targetId == HideTargetId)
            Messenger.Send(new BackdropDismissMessage());
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == HideTargetId)
            HideButtonProgress = progress;
    }
}
