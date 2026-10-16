namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Controls;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The T0(a) lag-mark button: a temporary, dwellable "Lag" overlay, shown only in an instrumented
/// session. One forgiving dwell (the visual grown 48 DIP per side, the probe's D3 decision) marks
/// the lag moment; the host then logs the dispatch queue's last 15 s with the completing sample's
/// timestamp. The label reads "Marked ✓" for a second after a mark, and the dwell re-arms only once
/// gaze has left the hit area, so one look cannot fire twice.
///
/// <para>
/// Placed at the work area's bottom-right, inset 24 DIP, moving up out of the chrome the activation
/// offer already keeps clear of (the edge bar, a docked toolbar, the keyboard, an open menu): the
/// button has a dwell target, and over the keys it would be dwelled by a gaze meant for them. It
/// follows chrome changes from a throttled gaze-sample check, like the restart notice card.
/// </para>
///
/// <para>
/// It never takes the foreground (every overlay is <c>WS_EX_NOACTIVATE</c>, set by
/// <see cref="OverlayWindow"/>), is topmost, is excluded from screen capture, and is click-through
/// like every overlay with a dwell target.
/// </para>
/// </summary>
public partial class LagMarkWindow : OverlayWindow, ILagMarkPresenter
{
    /// <summary>The dwell id of the mark button.</summary>
    internal const string TargetId = "lag_mark";

    /// <summary>The visible button's width in DIP (the drawn SurfaceLight fill).</summary>
    internal const double VisualWidthDip = 160;

    /// <summary>The visible button's height in DIP (the drawn SurfaceLight fill).</summary>
    internal const double VisualHeightDip = 96;

    /// <summary>The hit area is the visual grown by this on every side, in DIP (the probe's D3).</summary>
    internal const double HitGrowthDip = 48;

    /// <summary>How long "Marked" shows after a mark, in milliseconds.</summary>
    internal const int MarkedHoldMs = 1000;

    /// <summary>How often a gaze sample re-reads the chrome the button keeps clear of (microseconds).</summary>
    internal const long FollowCheckIntervalMicroseconds = 250_000;

    private readonly DwellDetector _dwell;
    private readonly int _dwellMs;
    private readonly IMessenger _messenger;
    private readonly DwellOutlineOverlay _outline;
    private readonly Func<IReadOnlyList<Rect>>? _keepClear;
    private readonly Action<LagMarkWindow>? _present;
    private IReadOnlyList<Rect> _placedAgainst = [];
    private long? _lastFollowCheckUs;
    private long _lastSampleTimestampUs;
    private bool _isShowing;
    private bool _rearmed = true;
    private DispatcherTimer? _markedTimer;

    /// <summary>Creates the window. Not shown until <see cref="ShowButton"/>.</summary>
    /// <param name="dwellMs">The mark button's dwell time; the toolbar-button dwell, per the spec.</param>
    /// <param name="messenger">The bus the dwell-completion cue uses; null is the app default.</param>
    /// <param name="keepClear">The rectangles, in DIP, the button must not cover: the edge bar,
    /// a docked toolbar, the shown keyboard, an open menu. Null means none.</param>
    /// <param name="present">How the button is put on screen; null shows it, topmost and without
    /// activating it. A seam for a headless test, which lays the button out instead: a shown overlay
    /// is the user's only input surface.</param>
    public LagMarkWindow(
        int dwellMs, IMessenger? messenger = null, Func<IReadOnlyList<Rect>>? keepClear = null,
        Action<LagMarkWindow>? present = null)
    {
        IsClickThrough = true; // read in OverlayWindow.OnSourceInitialized, so NOT set after init
        ExcludeFromScreenCapture = true;
        InitializeComponent();

        _dwellMs = dwellMs;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _keepClear = keepClear;
        _present = present;

        // The same outline every key and button shows its dwell progress with.
        var label = LabelText;
        ButtonBorder.Child = null;
        _outline = DwellOutlineOverlay.Attach(ButtonBorder, label);

        _dwell = new DwellDetector(cooldownMs: 0);
        _dwell.TargetActivated += OnDwellActivated;
        _dwell.TargetProgress += (_, progress) => _outline.SetProgress(progress);
    }

    /// <inheritdoc />
    public event Action<long>? Marked;

    /// <inheritdoc />
    public event Action? Moved;

    /// <inheritdoc />
    public bool IsShowing => _isShowing;

    /// <inheritdoc />
    public PhysicalRect HitBounds => new(
        DpiHelper.LogicalToPhysicalX(Left), DpiHelper.LogicalToPhysicalY(Top),
        DpiHelper.LogicalToPhysicalX(Width), DpiHelper.LogicalToPhysicalY(Height));

    /// <inheritdoc />
    public Rect ShownRectDip => new(Left, Top, Width, Height);

    /// <inheritdoc />
    public void ShowButton()
    {
        if (_isShowing) return;

        Prepare(SystemParameters.WorkArea);
        _isShowing = true;
        if (_present is not null)
            _present(this);
        else
            ShowWindow();

        // The hit area and the outline need a laid-out window at its final place.
        UpdateLayout();
        PlaceDwellTarget();
        _lastFollowCheckUs = null;
        _rearmed = true;
        RevertMarked();
    }

    private void ShowWindow()
    {
        if (!IsVisible)
            base.Show();
        RaiseToTop();
    }

    /// <inheritdoc />
    public void HideButton()
    {
        _markedTimer?.Stop();
        _dwell.ClearTargets();
        _outline.SetProgress(0);
        RevertMarked();
        _isShowing = false;
        if (IsVisible)
            Hide();
    }

    /// <inheritdoc />
    public void OnGaze(PhysicalPoint gaze, long timestampMicroseconds)
    {
        if (!_isShowing) return;
        _lastSampleTimestampUs = timestampMicroseconds;

        // The chrome the button keeps clear of can appear, go or move while it is up: follow it,
        // so the button never ends up over keys or the toolbar. Throttled: the read walks the
        // windows, and a gaze sample arrives at 60-90 Hz.
        if (_lastFollowCheckUs is not { } last || timestampMicroseconds - last >= FollowCheckIntervalMicroseconds)
        {
            _lastFollowCheckUs = timestampMicroseconds;
            ReplaceIfChromeMoved(SystemParameters.WorkArea);
        }

        // After a mark the dwell re-arms only once gaze has LEFT the hit area, so resting on the
        // button after a mark cannot produce a second one.
        if (!_rearmed && !_dwell.HitTest(TargetId, gaze))
            _rearmed = true;
        if (!_rearmed) return;

        _dwell.Update(gaze, timestampMicroseconds);
    }

    /// <summary>
    /// Re-places the button when the chrome it keeps clear of changed since it was placed. A no-op
    /// when nothing moved (moving the button restarts the dwell).
    /// </summary>
    /// <param name="workArea">The work area to place in, in DIP.</param>
    /// <returns>True when the button was moved.</returns>
    internal bool ReplaceIfChromeMoved(Rect workArea)
    {
        var chrome = _keepClear?.Invoke() ?? [];
        if (chrome.SequenceEqual(_placedAgainst)) return false;

        Prepare(workArea);
        UpdateLayout();
        PlaceDwellTarget();
        // The offer zone follows: the host re-publishes the button's hit area (review 01 F1).
        Moved?.Invoke();
        return true;
    }

    /// <summary>Places the button's full extent (its grown hit area) per <see cref="LagMarkPlacement"/>.</summary>
    /// <param name="workArea">The work area to place in, in DIP.</param>
    internal void Prepare(Rect workArea)
    {
        _placedAgainst = _keepClear?.Invoke() ?? [];
        (Left, Top) = LagMarkPlacement.Place(workArea, new Size(Width, Height), _placedAgainst);
    }

    /// <summary>Registers the whole window rectangle as the dwell target (the window IS the grown hit area).</summary>
    internal void PlaceDwellTarget()
    {
        _dwell.ClearTargets();
        _dwell.AddTarget(new DwellTarget(TargetId, HitBounds, _dwellMs));
    }

    /// <summary>
    /// The button's live dwell-target rects, or empty while it is hidden (second merge, the
    /// <c>RestartNoticeWindow.LiveTargetRects</c> pattern): the D17 gaze-consumer inventory reads
    /// its own detector's snapshot — the public <see cref="DwellDetector.Targets"/> copy-on-write
    /// array — so the preview panel's collision check can never disagree with what the button's
    /// gaze feed actually evaluates against (review F1's rule). The placed target is already in
    /// physical screen pixels (<see cref="HitBounds"/>) and is exactly the interactive surface,
    /// which is what D17 protects. Deliberately not a re-derivation from <c>Left/Top</c>: the
    /// snapshot moves with <see cref="PlaceDwellTarget"/> on the chrome-follow move, so a re-read
    /// here could race a move that has not re-placed the target yet.
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

    // Post-effect publish, like every dwell surface (spec D8): the mark line is the effect, and
    // the cue (the owner's setting) confirms it after.
    private void OnDwellActivated(string id)
    {
        _rearmed = false;
        ShowMarked();

        // The completing SAMPLE's timestamp, not the log-write time: the lag must not delay the mark.
        Marked?.Invoke(_lastSampleTimestampUs);
        _messenger.Send(new DwellActivationMessage());
    }

    /// <summary>Shows "Marked" for <see cref="MarkedHoldMs"/>, then reverts to the resting label.</summary>
    internal void ShowMarked()
    {
        LabelText.Text = StringTable.Current.T("overlays.lagmark.marked");
        if (_markedTimer is null)
        {
            _markedTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _markedTimer.Tick += (_, _) => RevertMarked();
        }

        _markedTimer.Stop();
        _markedTimer.Interval = TimeSpan.FromMilliseconds(MarkedHoldMs);
        _markedTimer.Start();
    }

    /// <summary>Reverts the label to its resting text and stops the hold timer.</summary>
    internal void RevertMarked()
    {
        _markedTimer?.Stop();
        LabelText.Text = StringTable.Current.T("overlays.lagmark.label");
    }
}
