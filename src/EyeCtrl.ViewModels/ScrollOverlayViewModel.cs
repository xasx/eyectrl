namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ScrollOverlayViewModel : DwellOverlayViewModelBase
{
    // The dot feedback's travel, in DIP (scaled at use: the anchor dot rides 24 DIP per unit).
    private const double MaxOffsetDip = 200.0;

    private readonly ScrollSettings _scrollSettings;
    private readonly DwellSettings _dwellSettings;
    private readonly Action<string>? _log;

    /// <summary>
    /// The dead zone around the anchor, in PHYSICAL px (scroll start fix, F-S3): the owner's own
    /// fixation radius, which is physical px by contract (<see cref="DwellSettings.FixationRadiusPixels"/>),
    /// never below the engine's legacy 30. At the default 80 this is the 80-px dead zone the
    /// investigation measured the 15-DIP one against.
    /// </summary>
    internal double DeadZoneRadiusPx =>
        Math.Max(ScrollEngine.DeadZoneRadiusPixels, _dwellSettings.FixationRadiusPixels);

    /// <summary>The stop button's own radius in physical px (50 DIP × the live scale): its whole
    /// drawn face stops the scroll, not its inner half.</summary>
    internal double StopZoneRadiusPx => DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius);

    /// <summary>The dot feedback's 200-DIP travel, in physical px (F-S3).</summary>
    internal double MaxOffsetPx => MaxOffsetDip * DpiHelper.ScaleX;

    private PhysicalPoint _anchor;
    private PhysicalPoint _stopButtonCenter;
    // Scroll start fix (F-S4): velocity only after the eye has first visited the dead zone around
    // the anchor. At entry the eye is on the menu's Scroll slot, ~900 px away, and applying the
    // first sample's velocity there was the lurch the owner reported.
    private bool _armed;
    private long _enteredAtMs;

    [ObservableProperty] private double _anchorDotOffsetX;
    [ObservableProperty] private double _anchorDotOffsetY;
    [ObservableProperty] private double _scrollVelocityX;
    [ObservableProperty] private double _scrollVelocityY;
    [ObservableProperty] private double _velocityMagnitude;

    public PhysicalPoint Anchor => _anchor;

    /// <summary>Whether the eye has visited the dead zone since entry: the window shows the crosshair
    /// prominently and the log reads it (scroll start fix, F-S4).</summary>
    public bool IsArmed => _armed;

    public event Action<double, double>? TargetVelocityChanged;
    public event Action? FeedbackChanged;

    public ScrollOverlayViewModel(
        IMessenger messenger, ScrollSettings scrollSettings, DwellSettings dwellSettings,
        Action<string>? log = null)
        : base(messenger, dwellSettings.DwellCooldownMs)
    {
        _scrollSettings = scrollSettings;
        _dwellSettings = dwellSettings;
        _log = log;
    }

    // Required by DwellOverlayViewModelBase but intentionally inert: the scroll stop dwell now
    // lives on the shared EdgeButtonBarViewModel, so this VM owns no dwell targets.
    protected override void OnTargetActivated(string targetId) { }
    protected override void OnTargetProgress(string targetId, double progress) { }

    // OnTargetActivated above is an empty handler — no effect ran, so there is nothing to announce.
    protected override bool AnnounceDwellActivation => false;

    public void EnterScrollMode(PhysicalPoint anchor, PhysicalPoint stopButtonCenter)
    {
        _anchor = anchor;
        _stopButtonCenter = stopButtonCenter;
        _armed = false;
        _enteredAtMs = Environment.TickCount64;
        IsVisible = true;
        AnchorDotOffsetX = 0;
        AnchorDotOffsetY = 0;
        ScrollVelocityX = 0;
        ScrollVelocityY = 0;
        VelocityMagnitude = 0;
    }

    public void ExitScrollMode()
    {
        IsVisible = false;
    }

    public override void Receive(GazeCursorMovedMessage message)
    {
        // Route through the shared sample gate (round-2 finding 2): this override replaced
        // base.Receive entirely, silently bypassing the pause gate — and its output IS OS input
        // (TargetVelocityChanged drives ScrollWorker → SendInput.Scroll). Unreachable today
        // (TogglePause refuses Paused from ScrollMode and fixation is dead while paused), but
        // the gate makes the stated contract true instead of aspirational.
        if (!IsVisible || !SampleMayDwell(message)) return;

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

        // F-S4: until the eye first visits the dead zone around the anchor, the ring follows the
        // dot feedback but nothing scrolls — the entry gaze (the menu's Scroll slot) must not
        // lurch the page. A visit arms the joystick for the rest of the session.
        if (!_armed)
        {
            if (dist <= DeadZoneRadiusPx)
            {
                _armed = true;
                _log?.Invoke($"Scroll armed after {Environment.TickCount64 - _enteredAtMs} ms");
            }
            else
            {
                ScrollVelocityX = 0;
                ScrollVelocityY = 0;
                VelocityMagnitude = 0;
                TargetVelocityChanged?.Invoke(0, 0);
                FeedbackChanged?.Invoke();
                return;
            }
        }

        var (vx, vy) = ScrollEngine.ComputeVelocity(
            message.Position, _anchor, _scrollSettings.Sensitivity,
            DeadZoneRadiusPx, DpiHelper.ScaleX, _stopButtonCenter, StopZoneRadiusPx);

        ScrollVelocityX = vx;
        ScrollVelocityY = vy;
        VelocityMagnitude = Math.Sqrt(vx * vx + vy * vy);

        TargetVelocityChanged?.Invoke(vx, vy);
        FeedbackChanged?.Invoke();
    }
}
