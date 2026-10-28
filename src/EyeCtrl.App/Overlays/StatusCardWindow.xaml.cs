namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// A status card (Gaze UX Wave B, B5): a bottom-centre banner that explains a control state the
/// first time it is entered in a session — Paused, TrackingLost, NoTracker. Modelled on
/// <see cref="DriftNoticeWindow"/>: the same 560 × 72 DIP banner, the same 8 s hold and 1 s fade,
/// click-through and <see cref="OverlayWindow.ExcludeFromScreenCapture"/>=true. It has NO dwell
/// target: the card informs, the paused strip acts. Only <see cref="StatusCardPresenter"/> shows it,
/// as a banner-slot occupant (B4), so it never overlaps a drift notice or a caregiver alert.
/// </summary>
public partial class StatusCardWindow : OverlayWindow
{
    private const double MarginDip = 48;
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(8);
    private static readonly Duration FadeOut = new(TimeSpan.FromSeconds(1));
    private DispatcherTimer? _hideTimer;
    private bool _positioned;

    /// <summary>
    /// Raised when the card has left the screen: its own hold/fade completed, or
    /// <see cref="HideCard"/> took it down at once. The banner slot advances on this.
    /// </summary>
    internal event EventHandler? Hidden;

    /// <summary>Creates the status-card window (click-through + excluded from screen capture, set before init).</summary>
    public StatusCardWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true;
        InitializeComponent();
    }

    /// <summary>Shows the card for one state for its 8 s hold (a re-show restarts the hold). Only
    /// the banner slot's presenter calls this: the window itself is never shown outside it (B4).</summary>
    public void ShowCard(ControlState state)
    {
        Message.Text = MessageFor(StringTable.Current, state);
        EnsurePositioned();
        // The drift banner's re-show discipline: a fade is started only by the hold-timer tick, and
        // removing a clock via BeginAnimation(dp, null) does not raise its Completed.
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

    /// <summary>Takes the card down at once, cancelling its hold and any fade in flight, and raises
    /// <see cref="Hidden"/> (census term (b): an immediate hide that closes the slot occupant).</summary>
    public void HideCard()
    {
        _hideTimer?.Stop();
        Banner.BeginAnimation(UIElement.OpacityProperty, null);
        Hide();
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The card's message for a state, resolved from the given table (a parameter, not the
    /// process global, so the mapping is pinnable without activating a table).</summary>
    /// <param name="strings">The table to resolve from.</param>
    /// <param name="state">A state that has a card: Paused, TrackingLost or NoTracker.</param>
    /// <returns>The card's one-line message.</returns>
    internal static string MessageFor(StringTable strings, ControlState state) => state switch
    {
        ControlState.Paused => strings.T("overlays.statusCard.paused"),
        ControlState.TrackingLost => strings.T("overlays.statusCard.trackingLost"),
        ControlState.NoTracker => strings.T("overlays.statusCard.noTracker"),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No status card for this state."),
    };

    private void OnHideTick(object? sender, EventArgs e)
    {
        _hideTimer?.Stop();
        var fade = new DoubleAnimation(1.0, 0.0, FadeOut) { FillBehavior = FillBehavior.Stop };
        fade.Completed += OnFadeCompleted;
        Banner.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>The fade has finished, so the card is off the screen. Named and <c>internal</c> so
    /// the slot's natural-closure path is pinnable headless (the B4 fixup-01 M1 lesson).</summary>
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

    /// <summary>The card's visible bounds in physical pixels while it is on screen (the banner
    /// slot's aggregate occluder source and the restart notice's keep-clear). True for as long as
    /// the window is visible, the fade tail included (census term (c)).</summary>
    public bool TryGetVisibleBounds(out PhysicalRect bounds)
    {
        bounds = default;
        if (!IsVisible)
            return false;
        bounds = new PhysicalRect(
            DpiHelper.LogicalToPhysicalX(Left),
            DpiHelper.LogicalToPhysicalY(Top),
            DpiHelper.LogicalToPhysicalX(Width),
            DpiHelper.LogicalToPhysicalY(Height));
        return true;
    }
}
