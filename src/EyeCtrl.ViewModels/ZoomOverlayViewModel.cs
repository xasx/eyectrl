namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ZoomOverlayViewModel : ObservableObject,
    IRecipient<StartZoomMessage>,
    IRecipient<EndZoomMessage>,
    IRecipient<GazeCursorMovedMessage>
{
    private const double SideButtonRadius = 50;
    private const double SideButtonGap = 20;
    private const int SideButtonDwellMs = 600;
    private const string CloseTargetId = "zoom_close";
    private const string RecaptureTargetId = "zoom_recapture";

    private readonly IMessenger _messenger;
    private readonly DwellDetector _dwellDetector;

    [ObservableProperty] private ScreenPoint _center = new(0, 0);
    [ObservableProperty] private int _radius;
    [ObservableProperty] private double _magnification;
    [ObservableProperty] private bool _isVisible;

    [ObservableProperty] private ScreenPoint _closeButtonCenter = new(0, 0);
    [ObservableProperty] private ScreenPoint _recaptureButtonCenter = new(0, 0);
    [ObservableProperty] private double _closeButtonProgress;
    [ObservableProperty] private double _recaptureButtonProgress;

    public ZoomOverlayViewModel(DwellSettings dwellSettings, IMessenger messenger)
    {
        _messenger = messenger;
        _dwellDetector = new DwellDetector(dwellSettings.DwellCooldownMs);
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        messenger.RegisterAll(this);
    }

    public void Receive(StartZoomMessage message)
    {
        Center = message.Center;
        Radius = message.Radius;
        Magnification = message.Magnification;

        // Compute side button positions: right side of zoom circle
        var displayRadius = message.Radius * message.Magnification;
        var buttonX = message.Center.X + displayRadius + SideButtonGap + SideButtonRadius;
        var topButtonY = message.Center.Y - SideButtonRadius - SideButtonGap / 2;
        var bottomButtonY = message.Center.Y + SideButtonRadius + SideButtonGap / 2;

        CloseButtonCenter = new ScreenPoint(buttonX, topButtonY);
        RecaptureButtonCenter = new ScreenPoint(buttonX, bottomButtonY);

        // Reset progress
        CloseButtonProgress = 0;
        RecaptureButtonProgress = 0;

        // Set up dwell targets
        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget(
            CloseTargetId, CloseButtonCenter, SideButtonRadius, SideButtonDwellMs));
        _dwellDetector.AddTarget(new DwellTarget(
            RecaptureTargetId, RecaptureButtonCenter, SideButtonRadius, SideButtonDwellMs));

        IsVisible = true;
    }

    public void Receive(EndZoomMessage message)
    {
        IsVisible = false;
        _dwellDetector.ClearTargets();
        CloseButtonProgress = 0;
        RecaptureButtonProgress = 0;
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            _dwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    private void OnTargetActivated(string targetId)
    {
        if (targetId == CloseTargetId)
            _messenger.Send(new ZoomCloseMessage());
        else if (targetId == RecaptureTargetId)
            _messenger.Send(new ZoomRecaptureMessage());
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == CloseTargetId)
            CloseButtonProgress = progress;
        else if (targetId == RecaptureTargetId)
            RecaptureButtonProgress = progress;
    }
}
