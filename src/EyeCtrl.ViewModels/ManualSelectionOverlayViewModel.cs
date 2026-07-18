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

    /// <summary>Whether the next show should capture a fresh screenshot (true) or reuse the existing
    /// one (false, e.g. navigating back from the action menu). Set before <see cref="IsVisible"/>.</summary>
    public bool Recapture { get; private set; } = true;

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
        Recapture = message.Recapture; // set before IsVisible so the show handler reads the right value

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
