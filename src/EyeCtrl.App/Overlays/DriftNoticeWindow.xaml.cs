namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EyeCtrl.App.Services;

/// <summary>
/// The report-only drift notice (design §7.1/§8.5): a bottom-center banner suggesting recalibration in Tobii that
/// holds ~8 s then fades out. It is click-through and <see cref="OverlayWindow.ExcludeFromScreenCapture"/>
/// =true, so it never intercepts gaze/mouse (no dwell targets — zero mis-activation surface) and is not
/// baked into the magnifier / action-menu capture. It only ever *shows a message* — it never modifies
/// calibration.
/// </summary>
public partial class DriftNoticeWindow : OverlayWindow, IDriftNoticePresenter
{
    private const double MarginDip = 48;
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(8);
    private static readonly Duration FadeOut = new(TimeSpan.FromSeconds(1));
    private DispatcherTimer? _hideTimer;
    private bool _positioned;

    /// <summary>Creates the drift-notice window (click-through + excluded from screen capture, set before init).</summary>
    public DriftNoticeWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true;
        InitializeComponent();
    }

    /// <inheritdoc />
    public void ShowDriftNotice()
    {
        EnsurePositioned();
        // Cancel any in-flight fade and re-show at full opacity. Unlike CueOverlayWindow's per-tick
        // re-flash (which needs a _fadeGeneration guard), a fade here is started ONLY by the hold-timer
        // tick, and removing a clock via BeginAnimation(dp, null) does not raise the removed clock's
        // Completed — so a re-show during a fade can't leave a stale Completed to hide the banner. No guard needed.
        Banner.BeginAnimation(UIElement.OpacityProperty, null);
        Banner.Opacity = 1.0;
        if (!IsVisible)
            base.Show();
        RaiseToTop();

        if (_hideTimer is null)
        {
            _hideTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = HoldTime };
            _hideTimer.Tick += OnHideTick;
        }
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        _hideTimer?.Stop();
        var fade = new DoubleAnimation(1.0, 0.0, FadeOut) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => Hide();
        Banner.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void EnsurePositioned()
    {
        if (_positioned)
            return;
        var area = SystemParameters.WorkArea; // DIPs, matching Window.Left/Top/Width/Height
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - MarginDip;
        _positioned = true;
    }
}
