namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// The report-only drift notice (design §7.1/§8.5): a bottom-center banner suggesting recalibration in Tobii that
/// holds ~8 s then fades out. It is click-through and <see cref="OverlayWindow.ExcludeFromScreenCapture"/>
/// =true, so it never intercepts gaze/mouse (no dwell targets — zero mis-activation surface) and is not
/// baked into the magnifier / action-menu capture. It only ever *shows a message* — it never modifies
/// calibration.
/// </summary>
public partial class DriftNoticeWindow : OverlayWindow
{
    private const double MarginDip = 48;
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(8);
    private static readonly Duration FadeOut = new(TimeSpan.FromSeconds(1));
    private DispatcherTimer? _hideTimer;
    private bool _positioned;

    /// <summary>
    /// Raised when the banner has left the screen: its own hold/fade completed, or
    /// <see cref="HideDriftNotice"/> took it down at once. The bottom-centre banner slot (B4)
    /// advances on this, so it never believes a banner it has already lost is still up.
    /// </summary>
    internal event EventHandler? Hidden;

    /// <summary>Creates the drift-notice window (click-through + excluded from screen capture, set before init).</summary>
    public DriftNoticeWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true;
        InitializeComponent();
    }

    /// <summary>Shows the notice for its 8 s hold (and restarts the hold if it is already up). Only
    /// the banner slot's presenter calls this: the window itself is never shown outside it (B4).</summary>
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

    /// <summary>
    /// Takes the banner down at once, cancelling its hold and any fade in flight. The banner slot
    /// uses this when a caregiver alert pre-empts the notice (B4): the window hides before the
    /// alert is shown, and the notice resumes at full opacity when it comes back.
    /// </summary>
    public void HideDriftNotice()
    {
        _hideTimer?.Stop();
        Banner.BeginAnimation(UIElement.OpacityProperty, null);
        Hide();
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        _hideTimer?.Stop();
        var fade = new DoubleAnimation(1.0, 0.0, FadeOut) { FillBehavior = FillBehavior.Stop };
        fade.Completed += OnFadeCompleted;
        Banner.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// The fade has finished, so the banner is off the screen. Named and <c>internal</c> so the
    /// banner slot's natural-closure path is pinnable headless (B4 fixup-01 M1): without this raise
    /// the slot keeps an invisible notice as its live occupant.
    /// </summary>
    internal void OnFadeCompleted(object? sender, EventArgs e)
    {
        Hide();
        Hidden?.Invoke(this, EventArgs.Empty);
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

    /// <summary>The banner's visible bounds in physical pixels while it is on screen (the banner
    /// slot's aggregate occluder source and the restart notice's keep-clear). False (and
    /// <paramref name="bounds"/> default) when it is not showing — the fade-out tail keeps reporting
    /// true until the window hides, which is the conservative reading.</summary>
    public bool TryGetVisibleBounds(out PhysicalRect bounds)
    {
        bounds = default;
        if (!IsVisible)
            return false;
        // The window's own rect in DIP, scaled to physical px (a length and a coordinate scale
        // identically — the helper just multiplies).
        bounds = new PhysicalRect(
            DpiHelper.LogicalToPhysicalX(Left),
            DpiHelper.LogicalToPhysicalY(Top),
            DpiHelper.LogicalToPhysicalX(Width),
            DpiHelper.LogicalToPhysicalY(Height));
        return true;
    }
}
