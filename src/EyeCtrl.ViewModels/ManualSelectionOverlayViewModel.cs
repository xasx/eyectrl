namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ManualSelectionOverlayViewModel : DwellOverlayViewModelBase,
    IRecipient<StartManualSelectionMessage>,
    IRecipient<EndManualSelectionMessage>,
    IRecipient<ManualSelectionHiddenMessage>
{
    [ObservableProperty] private PhysicalPoint _center = new(0, 0);
    [ObservableProperty] private int _radius;
    [ObservableProperty] private double _magnification;
    [ObservableProperty] private PhysicalPoint _gazePoint = new(0, 0);

    public ManualSelectionOverlayViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
    }

    public void Receive(StartManualSelectionMessage message)
    {
        Center = message.Center;
        Radius = message.Radius;
        Magnification = message.Magnification;
        GazePoint = message.GazePoint;

        IsVisible = true;
    }

    public void Receive(EndManualSelectionMessage message)
    {
        IsVisible = false;
    }

    public void Receive(ManualSelectionHiddenMessage message)
    {
        IsVisible = false;
    }

    // Required by DwellOverlayViewModelBase but intentionally inert: the close/recapture dwell now
    // lives on the shared EdgeButtonBarViewModel, so this VM owns no dwell targets.
    protected override void OnTargetActivated(string targetId) { }
    protected override void OnTargetProgress(string targetId, double progress) { }
}
