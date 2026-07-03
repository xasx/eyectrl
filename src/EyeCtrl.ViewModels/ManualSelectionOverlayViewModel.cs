namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ManualSelectionOverlayViewModel : ObservableObject,
    IRecipient<StartManualSelectionMessage>,
    IRecipient<EndManualSelectionMessage>,
    IRecipient<ManualSelectionHiddenMessage>,
    IRecipient<GazeCursorMovedMessage>,
    IDisposable
{
    private const string CloseTargetId = "ManualSelection_close";
    private const string RecaptureTargetId = "ManualSelection_recapture";

    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;
    private readonly DwellDetector _dwellDetector;
    private bool _disposed;

    [ObservableProperty] private PhysicalPoint _center = new(0, 0);
    [ObservableProperty] private int _radius;
    [ObservableProperty] private double _magnification;
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private PhysicalPoint _gazePoint = new(0, 0);

    [ObservableProperty] private PhysicalPoint _closeButtonCenter = new(0, 0);
    [ObservableProperty] private PhysicalPoint _recaptureButtonCenter = new(0, 0);
    [ObservableProperty] private double _closeButtonProgress;
    [ObservableProperty] private double _recaptureButtonProgress;

    public ManualSelectionOverlayViewModel(EyeCtrlSettings settings, IMessenger messenger)
    {
        _settings = settings;
        _messenger = messenger;
        _dwellDetector = new DwellDetector(settings.Dwell.DwellCooldownMs);
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        messenger.RegisterAll(this);
    }

    public void Receive(StartManualSelectionMessage message)
    {
        Center = message.Center;
        Radius = message.Radius;
        Magnification = message.Magnification;
        GazePoint = message.GazePoint;

        // Compute side button positions: right side of ManualSelection circle
        var physicalDisplayRadius = DpiHelper.LogicalToPhysicalX(message.Radius * message.Magnification);
        var physicalGap = DpiHelper.LogicalToPhysicalX(UIConstants.ManualSelectionSideButtonGap);
        var physicalBtnRadius = DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius);

        var buttonX = message.Center.X + physicalDisplayRadius + physicalGap + physicalBtnRadius;
        var topButtonY = message.Center.Y - physicalBtnRadius - physicalGap / 2;
        var bottomButtonY = message.Center.Y + physicalBtnRadius + physicalGap / 2;

        CloseButtonCenter = new PhysicalPoint(buttonX, topButtonY);
        RecaptureButtonCenter = new PhysicalPoint(buttonX, bottomButtonY);

        // Reset progress
        CloseButtonProgress = 0;
        RecaptureButtonProgress = 0;

        // Set up dwell targets
        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget(
            CloseTargetId, CloseButtonCenter, physicalBtnRadius, _settings.Dwell.ManualSelectionSideButtonDwellMs));
        _dwellDetector.AddTarget(new DwellTarget(
            RecaptureTargetId, RecaptureButtonCenter, physicalBtnRadius, _settings.Dwell.ManualSelectionSideButtonDwellMs));

        IsVisible = true;
    }

    public void Receive(EndManualSelectionMessage message)
    {
        IsVisible = false;
        _dwellDetector.ClearTargets();
        CloseButtonProgress = 0;
        RecaptureButtonProgress = 0;
    }

    public void Receive(ManualSelectionHiddenMessage message)
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
            _messenger.Send(new ManualSelectionCloseMessage());
        else if (targetId == RecaptureTargetId)
            _messenger.Send(new ManualSelectionRecaptureMessage());
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == CloseTargetId)
            CloseButtonProgress = progress;
        else if (targetId == RecaptureTargetId)
            RecaptureButtonProgress = progress;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _dwellDetector.TargetActivated -= OnTargetActivated;
        _dwellDetector.TargetProgress -= OnTargetProgress;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
