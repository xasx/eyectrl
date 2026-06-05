namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class CursorCircleViewModel : ObservableObject,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<StateChangedMessage>,
    IRecipient<ExternalDwellActiveMessage>,
    IDisposable
{
    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;
    private bool _disposed;

    // Effective visibility = the interaction state allows a cursor AND no external-dwell target
    // is currently active. Two independent inputs are combined into the single IsVisible so there
    // is exactly one owner of the property (no writer races between state changes and external dwell).
    private bool _stateAllowsCursor = true;
    private bool _externalDwellActive;

    [ObservableProperty] private PhysicalPoint _position = new(0, 0);
    [ObservableProperty] private bool _isVisible = true;

    public double Size => _settings.Cursor.CircleSize;
    public string CircleColor => _settings.Cursor.CircleColor;
    public double Opacity => _settings.Cursor.CircleOpacity;
    public double Thickness => _settings.Cursor.CircleThickness;

    public CursorCircleViewModel(EyeCtrlSettings settings, IMessenger messenger)
    {
        _settings = settings;
        _messenger = messenger;
        messenger.RegisterAll(this);
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            Position = message.Position;
    }

    public void Receive(StateChangedMessage message)
    {
        _stateAllowsCursor = message.NewState is InteractionState.FreeLook or InteractionState.Fixation;
        UpdateVisibility();
    }

    public void Receive(ExternalDwellActiveMessage message)
    {
        _externalDwellActive = message.Active;
        UpdateVisibility();
    }

    private void UpdateVisibility() => IsVisible = _stateAllowsCursor && !_externalDwellActive;

    public void Dispose()
    {
        if (_disposed)
            return;

        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
