namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ScrollOverlayViewModel : DwellOverlayViewModelBase
{
    private const double MaxOffsetPx = 200.0;
    private const double StopZoneRadiusPx = 50;

    private readonly ScrollSettings _scrollSettings;

    private PhysicalPoint _anchor;
    private PhysicalPoint _stopButtonCenter;

    [ObservableProperty] private double _anchorDotOffsetX;
    [ObservableProperty] private double _anchorDotOffsetY;
    [ObservableProperty] private double _scrollVelocityX;
    [ObservableProperty] private double _scrollVelocityY;
    [ObservableProperty] private double _velocityMagnitude;

    public PhysicalPoint Anchor => _anchor;
    public PhysicalPoint StopButtonCenter => default;

    public event Action<double, double>? TargetVelocityChanged;
    public event Action? FeedbackChanged;

    public ScrollOverlayViewModel(IMessenger messenger, ScrollSettings scrollSettings, DwellSettings dwellSettings)
        : base(messenger, dwellSettings.DwellCooldownMs)
    {
        _scrollSettings = scrollSettings;
    }

    protected override void OnTargetActivated(string targetId) { }
    protected override void OnTargetProgress(string targetId, double progress) { }

    public void EnterScrollMode(PhysicalPoint anchor, PhysicalPoint stopButtonCenter)
    {
        _anchor = anchor;
        _stopButtonCenter = stopButtonCenter;
        IsVisible = true;
        AnchorDotOffsetX = 0;
        AnchorDotOffsetY = 0;
        ScrollVelocityX = 0;
        ScrollVelocityY = 0;
        VelocityMagnitude = 0;

        DwellDetector.ClearTargets();
    }

    public void ExitScrollMode()
    {
        IsVisible = false;
        DwellDetector.ClearTargets();
    }

    public override void Receive(GazeCursorMovedMessage message)
    {
        if (!IsVisible) return;

        DwellDetector.Update(message.Position, message.TimestampMicroseconds);

        var (vx, vy) = ScrollEngine.ComputeVelocity(
            message.Position, _anchor, _scrollSettings.Sensitivity,
            _stopButtonCenter, StopZoneRadiusPx);

        double dx = message.Position.X - _anchor.X;
        double dy = _anchor.Y - message.Position.Y;
        double dist = message.Position.DistanceTo(_anchor);
        if (dist > 1.0)
        {
            AnchorDotOffsetX = Math.Clamp(dx / MaxOffsetPx, -1.0, 1.0);
            AnchorDotOffsetY = Math.Clamp(dy / MaxOffsetPx, -1.0, 1.0);
        }
        else
        {
            AnchorDotOffsetX = 0;
            AnchorDotOffsetY = 0;
        }

        ScrollVelocityX = vx;
        ScrollVelocityY = vy;
        VelocityMagnitude = Math.Sqrt(vx * vx + vy * vy);

        TargetVelocityChanged?.Invoke(vx, vy);
        FeedbackChanged?.Invoke();
    }
}
