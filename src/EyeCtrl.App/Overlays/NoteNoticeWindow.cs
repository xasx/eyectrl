namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// The capacity notice (P4 D4): "8 notes - remove one first". Click-through and target-free, it
/// only ever reports why a pin did nothing. It holds 3 s, then fades over 1 s (the
/// <see cref="DriftNoticeWindow"/> shape), and reports its visible bounds while showing - the
/// note gate reads it as an occluder (P2 D3), so a gaze resting on the notice can never fire a
/// note cell beneath it. One per manager, created lazily, never in DI; Dispose closes it.
/// </summary>
public sealed class NoteNoticeWindow : OverlayWindow
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(3);
    private static readonly Duration FadeOut = new(TimeSpan.FromSeconds(1));

    private readonly Border _card;
    private DispatcherTimer? _hideTimer;

    public NoteNoticeWindow()
    {
        IsClickThrough = true;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _card = new Border
        {
            Width = NoteLayout.NoticeCardWidthDip,
            Height = NoteLayout.NoticeCardHeightDip,
            Background = Tokens.BrushSurfaceDark,
            CornerRadius = Tokens.StandardCornerRadius,
            BorderBrush = Tokens.BrushAccent,
            BorderThickness = new Thickness(2),
            Child = new TextBlock
            {
                Text = StringTable.Current.T("overlays.note.capacity"),
                FontSize = Tokens.FontSizeBodyLarge,
                Foreground = Tokens.BrushTextPrimary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 16, 16, 16),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Content = _card;
    }

    /// <summary>Shows (or re-shows) the notice at a DIP rect the manager placed clear of every
    /// note card and chip. A re-show during a fade cancels the fade like the drift notice does.</summary>
    /// <summary>Test seam: headless STA tests run on the OWNER machine and must never put a
    /// window on its screen, so they skip the HWND while every other behaviour (the hold, the
    /// fade, the visible-bounds answer) runs unchanged.</summary>
    public bool ShowRealWindow = true;

    private bool _showingForTest;

    /// <param name="realHwnd">Whether the manager's show seam allows a REAL window (T6 fixup-09):
    /// a headless test run passes false, so the notice never paints even if a test forgot its own
    /// <see cref="ShowRealWindow"/> opt-out.</param>
    public void ShowAt(Rect dipRect, bool realHwnd = true)
    {
        _card.BeginAnimation(UIElement.OpacityProperty, null);
        _card.Opacity = 1.0;
        Left = dipRect.Left;
        Top = dipRect.Top;
        // The placement window rect (card + tail zone) sizes the window; _card draws it.
        Width = dipRect.Width;
        Height = dipRect.Height;
        if (ShowRealWindow && realHwnd)
        {
            if (!IsVisible)
                base.Show();
            RaiseToTop();
        }
        else
        {
            _showingForTest = true;
        }

        if (_hideTimer is null)
        {
            _hideTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = HoldTime };
            _hideTimer.Tick += OnHideTick;
        }
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>Test seams (review-01 survivors): drive the hold expiry without waiting 3 s of
    /// wall time, and read whether the fade has started.</summary>
    internal bool IsFadingForTest { get; private set; }

    /// <summary>Test seams (review-02 m1): the pinned durations, readable from the timer and the fade.</summary>
    internal TimeSpan HoldIntervalForTest => _hideTimer!.Interval;

    internal Duration FadeDurationForTest => FadeOut;

    internal void OnHoldElapsedForTest() => OnHideTick(this, EventArgs.Empty);

    private void OnHideTick(object? sender, EventArgs e)
    {
        IsFadingForTest = true;
        _hideTimer?.Stop();
        var fade = new DoubleAnimation(1.0, 0.0, FadeOut) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => { Hide(); _showingForTest = false; };
        _card.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>The visible bounds in physical px while showing (P2 D3). The fade-out tail keeps
    /// reporting true until the window hides - the conservative reading.</summary>
    public bool TryGetVisibleBounds(out PhysicalRect bounds)
    {
        bounds = default;
        // A hidden window is not displayed: the seam's own flag wins only while the HWND is
        // also not visible (T6 fixup-09).
        if (!IsVisible && !_showingForTest)
            return false;
        bounds = new PhysicalRect(
            DpiHelper.LogicalToPhysicalX(Left),
            DpiHelper.LogicalToPhysicalY(Top),
            DpiHelper.LogicalToPhysicalX(Width),
            DpiHelper.LogicalToPhysicalY(Height));
        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _hideTimer?.Stop();
    }
}
