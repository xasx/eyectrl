namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ActivationButtonViewModel : ObservableObject,
    IRecipient<ShowActivationButtonMessage>,
    IRecipient<HideActivationButtonMessage>,
    IRecipient<GazeCursorMovedMessage>
{
    private const string TargetId = "activate";
    private const double ButtonRadius = 50;

    private readonly DwellSettings _dwellSettings;
    private readonly IMessenger _messenger;
    private readonly DwellDetector _dwellDetector = new();

    [ObservableProperty] private ScreenPoint _position = new(0, 0);
    [ObservableProperty] private double _dwellProgress;
    [ObservableProperty] private bool _isVisible;

    public ActivationButtonViewModel(DwellSettings dwellSettings, IMessenger messenger)
    {
        _dwellSettings = dwellSettings;
        _messenger = messenger;

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
            message.Position, ButtonRadius,
            _dwellSettings.ActivationButtonDwellMs));
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
}
