namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
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

    /// <summary>
    /// Bumped on every recapture request. The magnifier's screenshot refresh keys off this
    /// property rather than <see cref="Center"/>: a recapture deliberately leaves Center/Radius/
    /// Magnification unchanged (the view stays wherever it was placed), so [ObservableProperty]'s
    /// equal-value short-circuit would otherwise swallow every recapture after the first. Same
    /// idiom as <c>ActionMenuViewModel.RingVersion</c>.
    /// </summary>
    [ObservableProperty] private int _recaptureVersion;

    /// <summary>Whether the next show should capture a fresh screenshot (true) or reuse the existing
    /// one (false, e.g. navigating back from the action menu). Set before <see cref="IsVisible"/>.</summary>
    public bool Recapture { get; private set; } = true;

    /// <summary>
    /// Requested by the recapture edge button. Bumps <see cref="RecaptureVersion"/> so the bound
    /// window re-captures at the existing geometry without moving.
    /// </summary>
    public void RequestRecapture() => RecaptureVersion++;

    public ManualSelectionOverlayViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.ManualSelection, sink, dwellRegistry)
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

    // OnTargetActivated above is an empty handler — no effect ran, so there is nothing to announce.
    protected override bool AnnounceDwellActivation => false;
}
