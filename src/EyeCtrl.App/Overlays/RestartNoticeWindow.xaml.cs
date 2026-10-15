namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Controls;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The restart notice shown when no Settings window is open to show it (holistic review F5): a
/// bottom-centre card with the notice text and ONE gaze-reachable button (the same "✕ Hide" /
/// "✓ OK" the Settings modal offers). It never takes the foreground (every overlay is
/// <c>WS_EX_NOACTIVATE</c>, set by <see cref="OverlayWindow"/>), is topmost, is excluded from screen
/// capture, and is click-through like every overlay with a dwell target; it closes itself after a
/// hold time whatever the gaze does, so it can neither trap a gaze-only user nor linger.
///
/// <para>
/// Placed like the caregiver alert: centred, 48 DIP above the bottom of the work area. Unlike the
/// caregiver alert it has a dwell button, so it also keeps clear of EyeCtrl's own chrome (the gaze
/// keyboard, a docked toolbar, an open menu: the zones the activation offer already avoids): it sits
/// just above or below whatever it would otherwise cover (<see cref="RestartNoticePlacement"/>), and
/// it moves if that chrome appears, goes or moves while the card is up.
/// </para>
///
/// <para>
/// While it shows it tells the activation-offer coordinator (<see cref="RestartNoticeActiveMessage"/>),
/// exactly as the eye-care banner does: it carries its own dwell target and must not compete with an
/// activation button, and because it is click-through a taken offer would click the window beneath it.
/// </para>
/// </summary>
public partial class RestartNoticeWindow : OverlayWindow, IRestartNoticePresenter
{
    /// <summary>The dwell id of the close button.</summary>
    internal const string CloseTargetId = "restart_notice_close";

    /// <summary>How often a gaze sample re-reads the chrome the card keeps clear of (microseconds).</summary>
    internal const long FollowCheckIntervalMicroseconds = 250_000;

    private readonly DwellDetector _dwell;
    private readonly int _dwellMs;
    private readonly IMessenger _messenger;
    private readonly DwellOutlineOverlay _outline;
    private readonly Func<IReadOnlyList<Rect>>? _keepClear;
    private readonly Action<RestartNoticeWindow>? _present;
    private IReadOnlyList<Rect> _placedAgainst = [];
    private long? _lastFollowCheckUs;
    private bool _isShowing;
    private DispatcherTimer? _holdTimer;

    /// <summary>Creates the window. Not shown until <see cref="ShowNotice"/>.</summary>
    /// <param name="dwellMs">The close button's dwell time; the app's activation dwell, like the eye-care banner.</param>
    /// <param name="messenger">The bus the dwell-completion cue and the activation-offer suspension use; null is the app default.</param>
    /// <param name="keepClear">The rectangles, in DIP, the card must not cover: the shown keyboard,
    /// a docked toolbar, an open menu. Null means none.</param>
    /// <param name="present">How the card is put on screen; null shows it, topmost and without
    /// activating it. A seam for a headless test, which lays the card out instead: a shown overlay is
    /// the user's only input surface.</param>
    public RestartNoticeWindow(
        int dwellMs = 700, IMessenger? messenger = null, Func<IReadOnlyList<Rect>>? keepClear = null,
        Action<RestartNoticeWindow>? present = null)
    {
        IsClickThrough = true; // read in OverlayWindow.OnSourceInitialized, so NOT set after init
        ExcludeFromScreenCapture = true;
        InitializeComponent();

        _dwellMs = dwellMs;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _keepClear = keepClear;
        _present = present;

        // The same outline every key and button shows its dwell progress with.
        var label = CloseLabel;
        CloseButton.Child = null;
        _outline = DwellOutlineOverlay.Attach(CloseButton, label);

        _dwell = new DwellDetector(cooldownMs: 0);
        _dwell.TargetActivated += OnDwellActivated;
        _dwell.TargetProgress += (_, progress) => _outline.SetProgress(progress);
    }

    /// <inheritdoc />
    public bool IsShowing => _isShowing;

    /// <summary>
    /// The card's live dwell-target rects, physical px (the D17 gaze-consumer inventory's
    /// restart-notice source): the close button's hit rectangle while the card shows — the surface
    /// this window's OWN detector is feeding, read as the detector's copy-on-write snapshot (the
    /// <c>NoteManager.LiveTargetRects</c> pattern, review F1) so the preview panel's collision check
    /// can never disagree with what the card's gaze feed actually evaluates against. Empty when the
    /// card is not showing (or before the first place) — a hidden card contributes nothing rather
    /// than a phantom rect at the origin.
    /// <para>
    /// Deliberately NOT the keyboard's <c>TryGetChromeBounds</c> shape: that one reads the whole
    /// window through <c>PointToScreen</c>, which needs a live presentation source and reports
    /// nothing without one — this card's own tests never show it (a shown overlay is the user's
    /// only input surface), so a presentation-source read would leave the source untested
    /// headlessly. The placed target is already in physical screen pixels (<see cref="CloseBounds"/>)
    /// and covers exactly the interactive surface, which is what D17 protects.
    /// </para>
    /// </summary>
    public IReadOnlyList<PhysicalRect> LiveTargetRects()
    {
        if (!_isShowing)
            return Array.Empty<PhysicalRect>();

        var targets = _dwell.Targets;
        if (targets.Count == 0)
            return Array.Empty<PhysicalRect>();

        var rects = new List<PhysicalRect>(targets.Count);
        foreach (var target in targets)
        {
            if (target.Bounds is { } bounds)
                rects.Add(bounds);
            else
                rects.Add(new PhysicalRect(
                    target.Center.X - target.Radius,
                    target.Center.Y - target.Radius,
                    target.Radius * 2,
                    target.Radius * 2));
        }
        return rects;
    }

    /// <inheritdoc />
    public void ShowNotice(string message, string closeLabel, TimeSpan hold)
    {
        SetText(message);
        CloseLabel.Text = closeLabel;
        _outline.SetProgress(0);

        Prepare(SystemParameters.WorkArea);
        _isShowing = true;
        if (_present is not null)
            _present(this);
        else
            ShowWindow();

        // The button's screen position needs a laid-out window at its final place.
        UpdateLayout();
        PlaceDwellTarget();
        _lastFollowCheckUs = null;

        // Offers yield while the card is up (see the class remarks).
        _messenger.Send(new RestartNoticeActiveMessage(true));

        if (_holdTimer is null)
        {
            _holdTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _holdTimer.Tick += (_, _) => HideNotice();
        }
        _holdTimer.Stop();
        _holdTimer.Interval = hold;
        _holdTimer.Start();
    }

    private void ShowWindow()
    {
        if (!IsVisible)
            base.Show();
        RaiseToTop();
    }

    /// <inheritdoc />
    public void OnGaze(PhysicalPoint gaze, long timestampMicroseconds)
    {
        if (!_isShowing) return;

        // The chrome the card keeps clear of can appear, go or move while the card is up: follow
        // it, so the card never ends up over keys or the toolbar. Throttled: the read walks the
        // windows, and a gaze sample arrives at 60-90 Hz.
        if (_lastFollowCheckUs is not { } last || timestampMicroseconds - last >= FollowCheckIntervalMicroseconds)
        {
            _lastFollowCheckUs = timestampMicroseconds;
            ReplaceIfChromeMoved(SystemParameters.WorkArea);
        }

        _dwell.Update(gaze, timestampMicroseconds);
    }

    /// <summary>
    /// Re-places the card when the chrome it keeps clear of changed since it was placed. A no-op when
    /// nothing moved (moving the card restarts the button's dwell).
    /// </summary>
    /// <param name="workArea">The work area to place in, in DIP.</param>
    /// <returns>True when the card was moved.</returns>
    internal bool ReplaceIfChromeMoved(Rect workArea)
    {
        var chrome = _keepClear?.Invoke() ?? [];
        if (chrome.SequenceEqual(_placedAgainst)) return false;

        Prepare(workArea);
        UpdateLayout();
        PlaceDwellTarget();
        return true;
    }

    /// <summary>
    /// The first line is the title (the theme's Display size, medium weight); the rest is body text
    /// (the theme's Title size). A message with no line break (a refusal reason) is all title: it is
    /// the one thing to read.
    /// </summary>
    internal void SetText(string message)
    {
        var split = message.Split('\n', 2);
        TitleText.Text = split[0];
        BodyText.Text = split.Length > 1 ? split[1] : string.Empty;
        BodyText.Visibility = split.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public void HideNotice()
    {
        _holdTimer?.Stop();
        _dwell.ClearTargets();
        _outline.SetProgress(0);
        _isShowing = false;
        if (IsVisible)
            Hide();
        // Sent unconditionally (the flag is idempotent), like the banner: a hide with nothing up is harmless.
        _messenger.Send(new RestartNoticeActiveMessage(false));
    }

    // Post-effect publish, like every dwell surface (spec D8): the notice goes down first, then
    // the cue confirms to the user what they just did.
    private void OnDwellActivated(string id)
    {
        HideNotice();
        _messenger.Send(new DwellActivationMessage());
    }

    /// <summary>
    /// Sizes the window to its text and places it. Class-C growth, as the caregiver alert: 1000 DIP
    /// wide, growing in height only from a minimum, so no translation is clipped. The text change
    /// invalidates the chain to the window, because a hidden window runs no layout pass and
    /// <c>Measure</c> would otherwise report the previous message's height.
    /// </summary>
    /// <param name="workArea">The work area to place in, in DIP.</param>
    internal void Prepare(Rect workArea)
    {
        for (DependencyObject? node = BodyText;
             node is not null && !ReferenceEquals(node, this);
             node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            (node as UIElement)?.InvalidateMeasure();
        }

        var content = (FrameworkElement)Content;
        content.Measure(new Size(Width, double.PositiveInfinity));
        var height = Math.Max(MinHeight, content.DesiredSize.Height);
        Height = height;

        _placedAgainst = _keepClear?.Invoke() ?? [];
        (Left, Top) = RestartNoticePlacement.Place(workArea, new Size(Width, height), _placedAgainst);
    }

    internal void PlaceDwellTarget()
    {
        // A shown window gets its layout from the HWND-driven pass; the headless present seam
        // cannot — a window-level Measure/Arrange stops at the window itself, so the button is
        // never arranged and its rect would place a zero-size phantom target. The present path
        // arranges the content subtree directly (the same measure Prepare runs, then the arrange
        // the real pass would do), on every place, so a re-placed card never reads a stale
        // arrangement. Production never enters this branch.
        if (_present is not null)
        {
            var content = (FrameworkElement)Content;
            content.Measure(new Size(Width, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, Width, Height));
        }

        // Relative to the window's content, which starts at the window's own origin (no frame, so the
        // client origin is the window origin, the property OverlayClientOriginTests gates). The
        // window itself is not a visual ancestor until it has a handle, so a headless test could not
        // use it.
        var inWindow = CloseButton.TransformToAncestor((Visual)Content)
            .TransformBounds(new Rect(CloseButton.RenderSize));
        _dwell.ClearTargets();
        _dwell.AddTarget(new DwellTarget(
            CloseTargetId,
            CloseBounds(new Rect(Left, Top, Width, Height), inWindow, DpiHelper.ScaleX, DpiHelper.ScaleY),
            _dwellMs));
    }

    /// <summary>
    /// The close button's hit area in physical pixels: the button's own rectangle (a rectangle, not
    /// a circle around its centre, so the whole drawn button is the target and nothing outside it).
    /// </summary>
    /// <param name="window">The window's rectangle in screen DIP.</param>
    /// <param name="buttonInWindow">The button's rectangle in window DIP.</param>
    /// <param name="scaleX">Physical pixels per DIP, horizontally.</param>
    /// <param name="scaleY">Physical pixels per DIP, vertically.</param>
    /// <returns>The hit rectangle in physical screen pixels.</returns>
    internal static PhysicalRect CloseBounds(Rect window, Rect buttonInWindow, double scaleX, double scaleY) =>
        new(
            (window.Left + buttonInWindow.Left) * scaleX,
            (window.Top + buttonInWindow.Top) * scaleY,
            buttonInWindow.Width * scaleX,
            buttonInWindow.Height * scaleY);
}
