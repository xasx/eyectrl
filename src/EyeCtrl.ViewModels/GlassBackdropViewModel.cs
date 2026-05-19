namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class GlassBackdropViewModel : ObservableObject,
    IRecipient<ShowBackdropMessage>,
    IRecipient<HideBackdropMessage>,
    IRecipient<GazeCursorMovedMessage>,
    IDisposable
{
    private const string HideTargetId = "backdrop_hide";

    private readonly IMessenger _messenger;
    private readonly DwellDetector _dwellDetector;
    private bool _disposed;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private PhysicalPoint _hideButtonCenter = new(0, 0);
    [ObservableProperty] private double _hideButtonProgress;

    public GlassBackdropViewModel(EyeCtrlSettings settings, IMessenger messenger)
    {
        _messenger = messenger;
        _dwellDetector = new DwellDetector(settings.Dwell.DwellCooldownMs);
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        messenger.RegisterAll(this);
    }

    public void Receive(ShowBackdropMessage message)
    {
        HideButtonCenter = message.HideButtonCenter;
        HideButtonProgress = 0;
        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget(
            HideTargetId,
            message.HideButtonCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            UIConstants.BackdropHideButtonDwellMs));
        IsVisible = true;
    }

    public void Receive(HideBackdropMessage message)
    {
        IsVisible = false;
        HideButtonProgress = 0;
        _dwellDetector.ClearTargets();
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            _dwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    private void OnTargetActivated(string targetId)
    {
        if (targetId == HideTargetId)
            _messenger.Send(new BackdropDismissMessage());
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == HideTargetId)
            HideButtonProgress = progress;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _dwellDetector.TargetActivated -= OnTargetActivated;
        _dwellDetector.TargetProgress -= OnTargetProgress;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
