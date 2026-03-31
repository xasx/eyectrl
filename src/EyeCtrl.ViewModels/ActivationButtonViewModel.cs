namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ActivationButtonViewModel : ObservableObject,
    IRecipient<ShowActivationButtonMessage>,
    IRecipient<HideActivationButtonMessage>,
    IRecipient<GazeCursorMovedMessage>,
    IDisposable
{
    private const string TargetId = "activate";

    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;
    private readonly DwellDetector _dwellDetector;
    private bool _disposed;

    [ObservableProperty] private ScreenPoint _position = new(0, 0);
    [ObservableProperty] private double _dwellProgress;
    [ObservableProperty] private bool _isVisible;

    public ActivationButtonViewModel(EyeCtrlSettings settings, IMessenger messenger)
    {
        _settings = settings;
        _messenger = messenger;
        _dwellDetector = new DwellDetector(settings.Dwell.DwellCooldownMs);

        _dwellDetector.TargetActivated += _ =>
        {
            _messenger.Send(new ActivationCompletedMessage());
        };
        _dwellDetector.TargetProgress += (_, progress) => DwellProgress = progress;

        messenger.RegisterAll(this);
    }

    public void Receive(ShowActivationButtonMessage message)
    {
        Position = message.Position;
        IsVisible = true;
        DwellProgress = 0;
        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget(TargetId,
            message.Position,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.ActivationButtonDwellMs));
    }

    public void Receive(HideActivationButtonMessage message)
    {
        IsVisible = false;
        DwellProgress = 0;
        _dwellDetector.ClearTargets();
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            _dwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
