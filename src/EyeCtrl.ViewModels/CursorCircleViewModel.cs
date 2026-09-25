namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class CursorCircleViewModel : MessengerViewModelBase,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<StateChangedMessage>,
    IRecipient<ExternalDwellActiveMessage>
{
    private readonly EyeCtrlSettings _settings;

    // Effective visibility = the interaction state allows a cursor AND no external-dwell target
    // is currently active. Two independent inputs are combined into the single IsVisible so there
    // is exactly one owner of the property (no writer races between state changes and external dwell).
    private bool _stateAllowsCursor = true;
    private bool _externalDwellActive;

    [ObservableProperty] private PhysicalPoint _position = new(0, 0);
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>
    /// True while the interaction state is Paused. Paused does not hide the ring: it stays visible
    /// (dimmed by the View) so the user can see that tracking is alive and where their gaze is.
    /// </summary>
    [ObservableProperty] private bool _isPaused;

    public double Size => _settings.Cursor.CircleSize;
    public string CircleColor => _settings.Cursor.CircleColor;
    public double Opacity => _settings.Cursor.CircleOpacity;
    public double Thickness => _settings.Cursor.CircleThickness;

    public CursorCircleViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger)
    {
        _settings = settings;
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            Position = message.Position;
    }

    public void Receive(StateChangedMessage message)
    {
        // Paused keeps the ring (dimmed) — it is the user's only evidence that tracking is alive
        // while they are looking for the resume target. RepeatMode hides the normal circle: the
        // contracting repeat cursor replaces it there.
        IsPaused = message.NewState == InteractionState.Paused;
        _stateAllowsCursor = message.NewState
            is InteractionState.FreeLook or InteractionState.Fixation or InteractionState.Paused;
        UpdateVisibility();
    }

    public void Receive(ExternalDwellActiveMessage message)
    {
        _externalDwellActive = message.Active;
        UpdateVisibility();
    }

    private void UpdateVisibility() => IsVisible = _stateAllowsCursor && !_externalDwellActive;
}
