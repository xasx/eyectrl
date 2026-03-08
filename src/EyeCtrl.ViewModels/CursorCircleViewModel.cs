namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class CursorCircleViewModel : ObservableObject,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<StateChangedMessage>
{
    private readonly CursorSettings _settings;

    [ObservableProperty] private ScreenPoint _position = new(0, 0);
    [ObservableProperty] private bool _isVisible = true;

    public double Size => _settings.CircleSize;
    public string CircleColor => _settings.CircleColor;
    public double Opacity => _settings.CircleOpacity;
    public double Thickness => _settings.CircleThickness;

    public CursorCircleViewModel(CursorSettings settings, IMessenger messenger)
    {
        _settings = settings;
        messenger.RegisterAll(this);
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            Position = message.Position;
    }

    public void Receive(StateChangedMessage message)
    {
        IsVisible = message.NewState is InteractionState.FreeLook or InteractionState.Fixation;
    }
}
