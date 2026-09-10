namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The locked-mode aiming cursor (design D27, owner spec): a smaller circle that CONTRACTS as the
/// locked-action dwell builds and snaps back to full size when the action fires — the mode's only
/// dwell-progress feedback, replacing the normal cursor circle while it runs. Follows the
/// GazeCursorMovedMessage stream like the normal circle; visibility is state-driven.
/// </summary>
public partial class RepeatCursorViewModel : MessengerViewModelBase,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<StateChangedMessage>,
    IRecipient<RepeatDwellProgressChangedMessage>
{
    public RepeatCursorViewModel(IMessenger messenger) : base(messenger) { }

    [ObservableProperty] private PhysicalPoint _position = new(0, 0);
    [ObservableProperty] private double _dwellProgress;
    [ObservableProperty] private bool _isVisible;

    public void Receive(GazeCursorMovedMessage message) => Position = message.Position;

    public void Receive(StateChangedMessage message) => IsVisible = message.NewState == InteractionState.RepeatMode;

    public void Receive(RepeatDwellProgressChangedMessage message) => DwellProgress = message.Progress;
}
