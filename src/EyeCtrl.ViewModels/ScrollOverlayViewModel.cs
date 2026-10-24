namespace EyeCtrl.ViewModels;

using System.Globalization;
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
    public double DeadZoneRadiusPx =>
        Math.Max(ScrollEngine.DeadZoneRadiusPixels, _dwellSettings.FixationRadiusPixels);

    /// <summary>The start circle's radius in DIP (scroll arming fix, D1): looking anywhere inside
    /// it for <see cref="StartHoldMs"/> starts scrolling. 450 DIP across — a good deal wider than the menu's
    /// central circle — so this tracker's typical error no longer decides whether it starts.</summary>
    public const double StartRadiusDip = 225;

    /// <summary>How long, in ms of sample time, the gaze must stay inside the start circle to arm
    /// (D3): a short fixation rather than one sample, so a passing glance does not start it.</summary>
    public const int StartHoldMs = 150;

    /// <summary>The start circle's radius in PHYSICAL px: <see cref="StartRadiusDip"/> × the live
    /// scale (450 px at 200 %), never below the dead zone. Arming uses this; the dead zone itself
    /// (<see cref="DeadZoneRadiusPx"/>) is unchanged.</summary>
    public double StartRadiusPx => Math.Max(DeadZoneRadiusPx, StartRadiusDip * DpiHelper.ScaleX);

    /// <summary>The stop button's own radius in physical px (50 DIP × the live scale): its whole
    /// drawn face stops the scroll, not its inner half.</summary>
    public double StopZoneRadiusPx => DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius);

    /// <summary>The dot feedback's 200-DIP travel, in physical px (F-S3).</summary>
    internal double MaxOffsetPx => MaxOffsetDip * DpiHelper.ScaleX;

    private PhysicalPoint _anchor;
    private PhysicalPoint _stopButtonCenter;
    // Scroll start fix (F-S4): velocity only after the eye has first visited the dead zone around
    // the anchor. At entry the eye is on the menu's Scroll slot, ~900 px away, and applying the
    // first sample's velocity there was the lurch the owner reported.
    private bool _armed;
    private long _enteredAtMs;

    // Scroll arming fix (C2/C3/C5): the unarmed fixation run — consecutive gated samples inside the
    // start circle: its first timestamp, its sample count and coordinate sums (for the centroid).
    private long _runStartUs;
    private int _runCount;
    private double _runSumX;
    private double _runSumY;
    // The joystick's neutral point: the anchor at entry, the arming fixation's centroid once armed.
    // The scroll TARGET and the crosshair stay on the anchor.
    private PhysicalPoint _restPoint;
    // The closest any gated sample came to the anchor while unarmed (-1 until the first), for the log.
    private double _nearestPx = -1;

    [ObservableProperty] private double _anchorDotOffsetX;
    [ObservableProperty] private double _anchorDotOffsetY;
    [ObservableProperty] private double _scrollVelocityX;
    [ObservableProperty] private double _scrollVelocityY;
    [ObservableProperty] private double _velocityMagnitude;

    public PhysicalPoint Anchor => _anchor;

    /// <summary>The stop button's centre in physical px — the centre of the engine's stop zone.
    /// Test seam (InternalsVisibleTo): the App's entry wiring must place the drawn button and this
    /// zone at the same spot.</summary>
    internal PhysicalPoint StopButtonCenter => _stopButtonCenter;

    /// <summary>The joystick's neutral point: the anchor until the arming fixation, then that
    /// fixation's centroid (D2). Velocity and the dot feedback are measured from it.</summary>
    public PhysicalPoint RestPoint => _restPoint;

    /// <summary>The minimum distance of any gated sample to the anchor while unarmed, in physical
    /// px; -1 until the first. Frozen once armed.</summary>
    public double NearestPx => _nearestPx;

    /// <summary>Whether the eye has visited the dead zone since entry. The window uses it as the
    /// arming cue (the crosshair is full-opacity and double-thick until the first visit) and the
    /// log reads it (scroll start fix, F-S4; cue added by fixup 01 F2).</summary>
    public bool IsArmed => _armed;

    public event Action<double, double>? TargetVelocityChanged;
    public event Action? FeedbackChanged;

    public ScrollOverlayViewModel(
        IMessenger messenger, ScrollSettings scrollSettings, DwellSettings dwellSettings,
        Action<string>? log = null)
        : base(messenger, dwellSettings.DwellCooldownMs, pauseGraceSettings: dwellSettings)
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
        _restPoint = anchor;
        _stopButtonCenter = stopButtonCenter;
        _armed = false;
        ResetRun();
        _nearestPx = -1;
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
        // (TargetVelocityChanged drives ScrollWorker → SendInput.Scroll). A5's Pause stops the
        // worker before the state flips, so this is a belt-and-braces gate now; it keeps the
        // stated contract true instead of aspirational.
        if (!IsVisible) return;
        if (!SampleMayDwell(message))
        {
            // A dropped sample (gap, paused) breaks the arming run: the fixation must be unbroken.
            if (!_armed) ResetRun();
            return;
        }

        // F-S4 + the arming fix: until the eye has FIXATED inside the start circle around the
        // anchor (StartHoldMs of unbroken samples), the ring follows the dot feedback but nothing
        // scrolls — the entry gaze (the menu's Scroll slot) must not lurch the page. The fixation
        // arms the joystick for the rest of the session and its centroid becomes the rest point.
        if (!_armed)
        {
            double anchorDist = message.Position.DistanceTo(_anchor);
            if (_nearestPx < 0 || anchorDist < _nearestPx) _nearestPx = anchorDist;

            if (anchorDist <= StartRadiusPx)
            {
                if (_runCount == 0) _runStartUs = message.TimestampMicroseconds;
                _runCount++;
                _runSumX += message.Position.X;
                _runSumY += message.Position.Y;

                if (message.TimestampMicroseconds - _runStartUs >= StartHoldMs * 1000L)
                {
                    _armed = true;
                    _restPoint = new PhysicalPoint(_runSumX / _runCount, _runSumY / _runCount);
                    _log?.Invoke(string.Format(
                        CultureInfo.InvariantCulture,
                        "Scroll armed after {0} ms (offset=({1:F0},{2:F0})px, nearest={3:F0}px)",
                        Environment.TickCount64 - _enteredAtMs,
                        Whole(_restPoint.X - _anchor.X), Whole(_restPoint.Y - _anchor.Y), _nearestPx));
                }
            }
            else
            {
                ResetRun();
            }
        }

        // The dot rides the gaze relative to the rest point, so what is drawn is what is controlled.
        double dx = message.Position.X - _restPoint.X;
        double dy = _restPoint.Y - message.Position.Y;
        if (message.Position.DistanceTo(_restPoint) > 1.0)
        {
            AnchorDotOffsetX = Math.Clamp(dx / MaxOffsetPx, -1.0, 1.0);
            AnchorDotOffsetY = Math.Clamp(dy / MaxOffsetPx, -1.0, 1.0);
        }
        else
        {
            AnchorDotOffsetX = 0;
            AnchorDotOffsetY = 0;
        }

        if (!_armed)
        {
            ScrollVelocityX = 0;
            ScrollVelocityY = 0;
            VelocityMagnitude = 0;
            TargetVelocityChanged?.Invoke(0, 0);
            FeedbackChanged?.Invoke();
            return;
        }

        var (vx, vy) = ScrollEngine.ComputeVelocity(
            message.Position, _restPoint, _scrollSettings.Sensitivity,
            DeadZoneRadiusPx, DpiHelper.ScaleX, _stopButtonCenter, StopZoneRadiusPx);

        ScrollVelocityX = vx;
        ScrollVelocityY = vy;
        VelocityMagnitude = Math.Sqrt(vx * vx + vy * vy);

        TargetVelocityChanged?.Invoke(vx, vy);
        FeedbackChanged?.Invoke();
    }

    private void ResetRun()
    {
        _runCount = 0;
        _runStartUs = 0;
        _runSumX = 0;
        _runSumY = 0;
    }

    // Rounds for the log and folds -0 into 0, so a tiny negative offset never prints "-0".
    private static double Whole(double v) => Math.Round(v) + 0.0;
}
