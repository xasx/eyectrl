namespace EyeCtrl.App.Overlays;

using System;
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
/// Placed like the caregiver alert: centred, 48 DIP above the bottom of the work area. That stays
/// clear of the toolbar (docked to the top by default and, when moved, to an edge) and the right-hand
/// edge bars, and a window that grows for a longer German text grows upward from a fixed bottom edge.
/// Unlike the caregiver alert it has a dwell button, so it also never covers the gaze keyboard: while
/// the keyboard is shown the card sits just above its top edge (<see cref="RestartNoticePlacement"/>),
/// and it moves if the keyboard appears or goes while the card is up.
/// </para>
/// </summary>
public partial class RestartNoticeWindow : OverlayWindow, IRestartNoticePresenter
{
    /// <summary>The dwell id of the close button.</summary>
    internal const string CloseTargetId = "restart_notice_close";

    private readonly DwellDetector _dwell;
    private readonly int _dwellMs;
    private readonly IMessenger _messenger;
    private readonly DwellOutlineOverlay _outline;
    private readonly Func<Rect?>? _keyboardBounds;
    private Rect? _placedAgainst;
    private DispatcherTimer? _holdTimer;

    /// <summary>Creates the window. Not shown until <see cref="ShowNotice"/>.</summary>
    /// <param name="dwellMs">The close button's dwell time; the app's activation dwell, like the eye-care banner.</param>
    /// <param name="messenger">The bus the dwell-completion cue listens on; null is the app default.</param>
    /// <param name="keyboardBounds">The shown gaze keyboard's bounds in DIP, or null when it is not
    /// shown; the card keeps clear of it. Null (the parameter) means no keyboard to avoid.</param>
    public RestartNoticeWindow(int dwellMs = 700, IMessenger? messenger = null, Func<Rect?>? keyboardBounds = null)
    {
        IsClickThrough = true; // read in OverlayWindow.OnSourceInitialized, so NOT set after init
        ExcludeFromScreenCapture = true;
        InitializeComponent();

        _dwellMs = dwellMs;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _keyboardBounds = keyboardBounds;

        // The same outline every key and button shows its dwell progress with.
        var label = CloseLabel;
        CloseButton.Child = null;
        _outline = DwellOutlineOverlay.Attach(CloseButton, label);

        _dwell = new DwellDetector(cooldownMs: 0);
        _dwell.TargetActivated += OnDwellActivated;
        _dwell.TargetProgress += (_, progress) => _outline.SetProgress(progress);
    }

    /// <inheritdoc />
    public bool IsShowing => IsVisible;

    /// <inheritdoc />
    public void ShowNotice(string message, string closeLabel, TimeSpan hold)
    {
        SetText(message);
        CloseLabel.Text = closeLabel;
        _outline.SetProgress(0);

        Prepare(SystemParameters.WorkArea);
        if (!IsVisible)
            base.Show();
        RaiseToTop();

        // The button's screen position needs a laid-out window at its final place.
        UpdateLayout();
        PlaceDwellTarget();

        if (_holdTimer is null)
        {
            _holdTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _holdTimer.Tick += (_, _) => HideNotice();
        }
        _holdTimer.Stop();
        _holdTimer.Interval = hold;
        _holdTimer.Start();
    }

    /// <inheritdoc />
    public void OnGaze(PhysicalPoint gaze, long timestampMicroseconds)
    {
        // The keyboard can appear (or go) while the card is up: follow it, so the card never ends
        // up over keys. The card moves, and its dwell target with it.
        if (IsVisible)
            ReplaceIfKeyboardMoved(SystemParameters.WorkArea);
        _dwell.Update(gaze, timestampMicroseconds);
    }

    /// <summary>
    /// Re-places the card when the keyboard's bounds changed since it was placed. A no-op when
    /// nothing moved (a gaze sample calls this while the card is shown, and moving the card
    /// restarts the button's dwell).
    /// </summary>
    /// <param name="workArea">The work area to place in, in DIP.</param>
    /// <returns>True when the card was moved.</returns>
    internal bool ReplaceIfKeyboardMoved(Rect workArea)
    {
        var keyboard = _keyboardBounds?.Invoke();
        if (Equals(keyboard, _placedAgainst)) return false;

        Prepare(workArea);
        UpdateLayout();
        PlaceDwellTarget();
        return true;
    }

    /// <summary>
    /// The first line is the title (theme title size, medium weight); the rest is body text. A
    /// message with no line break (a refusal reason) is all title: it is the one thing to read.
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
        if (IsVisible)
            Hide();
    }

    // Post-effect publish, like every dwell surface (spec D8): the notice goes down first, then
    // the cue confirms to the user what they just did.
    private void OnDwellActivated(string id)
    {
        HideNotice();
        _messenger.Send(new DwellActivationMessage());
    }

    /// <summary>
    /// Sizes the window to its text and places it. Class-C growth, as the caregiver alert: 920 DIP
    /// wide, growing in height only from a minimum, so no translation is clipped. The text change
    /// invalidates the chain to the window, because a hidden window runs no layout pass and
    /// <c>Measure</c> would otherwise report the previous message's height.
    /// </summary>
    /// <param name="workArea">The work area to anchor in, in DIP.</param>
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

        _placedAgainst = _keyboardBounds?.Invoke();
        (Left, Top) = RestartNoticePlacement.Place(workArea, new Size(Width, height), _placedAgainst);
    }

    internal void PlaceDwellTarget()
    {
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
