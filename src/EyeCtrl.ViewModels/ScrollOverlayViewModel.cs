namespace EyeCtrl.ViewModels;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ScrollOverlayViewModel : ObservableObject,
    IRecipient<GazeCursorMovedMessage>,
    IDisposable
{
    private const string StopButtonTargetId = "scroll-stop";
    private const int StopButtonDwellMs = 600;
    private const double StopButtonRadiusPx = 30;
    private const double StopZoneRadiusPx = 50;
    private const double MaxOffsetPx = 200.0;

    private readonly DwellDetector _dwellDetector;
    private readonly IMessenger _messenger;
    private readonly ScrollSettings _scrollSettings;
    private bool _disposed;

    private PhysicalPoint _anchor;
    private PhysicalPoint _stopButtonCenter;

    [ObservableProperty] private double _anchorDotOffsetX;
    [ObservableProperty] private double _anchorDotOffsetY;
    [ObservableProperty] private double _scrollVelocityX;
    [ObservableProperty] private double _scrollVelocityY;
    [ObservableProperty] private double _stopButtonProgress;
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private double _velocityMagnitude;

    public PhysicalPoint Anchor => _anchor;
    public PhysicalPoint StopButtonCenter => _stopButtonCenter;

    public event Action? ExitRequested;
    public event Action<double, double>? TargetVelocityChanged;
    public event Action? FeedbackChanged;

    public ScrollOverlayViewModel(ScrollSettings scrollSettings, IMessenger messenger)
    {
        _scrollSettings = scrollSettings;
        _messenger = messenger;
        _dwellDetector = new DwellDetector();
        _dwellDetector.TargetActivated += _ => ExitRequested?.Invoke();
        _dwellDetector.TargetProgress += (_, progress) => StopButtonProgress = progress;
        messenger.RegisterAll(this);
    }

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
        StopButtonProgress = 0;

        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget(
            StopButtonTargetId,
            _stopButtonCenter,
            StopButtonRadiusPx,
            StopButtonDwellMs));
    }

    public void ExitScrollMode()
    {
        IsVisible = false;
        _dwellDetector.ClearTargets();
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (!IsVisible) return;

        _dwellDetector.Update(message.Position, message.TimestampMicroseconds);

        var (vx, vy) = ScrollEngine.ComputeVelocity(
            message.Position, _anchor, _scrollSettings.Sensitivity,
            _stopButtonCenter, StopZoneRadiusPx);

        double dx = message.Position.X - _anchor.X;
        double dy = _anchor.Y - message.Position.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);
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

    public void Dispose()
    {
        if (_disposed) return;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
