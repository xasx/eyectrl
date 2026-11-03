namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Controls;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// C10's blocked-input banner: a full-width strip at the top of the screen that says, before any
/// interaction, that Windows is discarding EyeCtrl's clicks — plus two gaze-only exits that need no
/// injected input to reach.
///
/// <para>
/// Why a separate surface rather than an occupant of the bottom banner slot: a blocked-input state
/// is an emergency (the owner's only input channel is silently gone), so it must never wait behind
/// another banner. It is click-through and excluded from screen capture like every overlay, never
/// takes the foreground, and carries its own dwell detector over its two buttons.
/// </para>
///
/// <para>
/// Shown and hidden from the UI thread by <see cref="Services.BlockedInputCoordinator"/>; the
/// window owns only its text, geometry and dwell. The escape action is delegated through
/// <see cref="EscapeRequested"/> so the window never touches the window manager itself.
/// </para>
/// </summary>
public partial class BlockedInputBannerWindow : OverlayWindow, IBlockedInputBanner
{
    /// <summary>The dwell id of the "to desktop / previous window" button.</summary>
    internal const string EscapeTargetId = "blocked_input_escape";

    /// <summary>The dwell id of the dismiss button.</summary>
    internal const string DismissTargetId = "blocked_input_dismiss";

    /// <summary>The message strip's height in DIP (the XAML Border's Height).</summary>
    internal const double MessageStripHeightDip = 96;

    /// <summary>The two buttons' height in DIP.</summary>
    internal const double ButtonHeightDip = 100;

    /// <summary>Room below the buttons in DIP.</summary>
    internal const double ButtonBottomBandDip = 44;

    private readonly DwellDetector _dwell;
    private readonly int? _fallbackDwellMs;
    private readonly DwellSettings _dwellSettings;
    private readonly ToolbarSettings _toolbarSettings;
    private readonly IMessenger _messenger;
    private readonly Action<BlockedInputBannerWindow>? _present;
    private readonly DwellOutlineOverlay _escapeOutline;
    private readonly DwellOutlineOverlay _dismissOutline;
    private bool _isShowing;

    /// <summary>Creates the banner. Not shown until <see cref="ShowBlocked"/>.</summary>
    /// <param name="dwellMs">The buttons' dwell used only when no live <paramref name="dwellSettings"/>
    /// is supplied (tests).</param>
    /// <param name="dwellRegistry">Registers the detector with the live dwell routing, like every
    /// other dwell surface.</param>
    /// <param name="dwellSettings">The live settings object; its grace and button dwell are read at
    /// every show.</param>
    /// <param name="toolbarSettings">The live toolbar settings, for the ring/button placement below
    /// a top-docked toolbar.</param>
    /// <param name="messenger">The dwell-completion cue bus; null is the app default.</param>
    /// <param name="present">How the banner is put on screen; null shows it topmost without
    /// activating it. A headless test supplies a callback and lays the content out instead.</param>
    public BlockedInputBannerWindow(
        int dwellMs = 700,
        IDwellDetectorRegistry? dwellRegistry = null,
        DwellSettings? dwellSettings = null,
        ToolbarSettings? toolbarSettings = null,
        IMessenger? messenger = null,
        Action<BlockedInputBannerWindow>? present = null)
    {
        IsClickThrough = true; // read in OverlayWindow.OnSourceInitialized, so NOT after init
        ExcludeFromScreenCapture = true;
        InitializeComponent();

        _dwellSettings = dwellSettings ?? new DwellSettings();
        _fallbackDwellMs = dwellSettings is null ? dwellMs : null;
        _toolbarSettings = toolbarSettings ?? new ToolbarSettings();
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _present = present;

        _escapeOutline = DwellOutlineOverlay.Attach(EscapeButton, Detach(EscapeButton, EscapeLabel));
        _dismissOutline = DwellOutlineOverlay.Attach(DismissButton, Detach(DismissButton, DismissLabel));

        // A 1 s cooldown, unlike the eye-care surfaces (which hide themselves on activation): the
        // escape button must stay up when the attempt fails, so a sustained dwell would otherwise
        // re-run SetForegroundWindow on every sample. One attempt per second is harmless and lets
        // a retry succeed once the desktop settles.
        _dwell = new DwellDetector(
            cooldownMs: 1000, context: DwellContext.Other, pauseGraceMs: _dwellSettings.DwellPauseGraceMs)
        {
            OtherTargetReleasesHold = true,
        };
        _dwell.TargetActivated += OnTargetActivated;
        _dwell.TargetProgress += OnTargetProgress;

        dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            _dwell, DwellContext.Other,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs));

        ApplyGeometry();
    }

    /// <summary>Raised when the owner dwells the "to desktop / previous window" button.</summary>
    public event EventHandler? EscapeRequested;

    /// <summary>Raised when the owner dwells the dismiss button (the window then hides itself).</summary>
    public event EventHandler? DismissRequested;

    /// <summary>
    /// Raised AFTER the banner actually left the screen (C10 fixup-04). The note gate latches the
    /// targets under the held gaze here: the emergency buttons sat over a possible note ✕, so a
    /// gaze that never left the spot must not reach the note's own dwell.
    /// </summary>
    public event EventHandler? Hidden;

    /// <summary>The live dwell detector (tests drive the buttons' progress through it).</summary>
    internal DwellDetector DwellForTest => _dwell;

    /// <summary>The band the buttons' top edge sits at, in DIP — the placement test seam.</summary>
    internal double ButtonsTopForTest => ButtonsTopDip();

    /// <summary>Whether the banner is currently up.</summary>
    internal bool IsShowingForTest => _isShowing;

    /// <summary>The buttons' live dwell time in milliseconds.</summary>
    internal int DwellMs => _fallbackDwellMs ?? _dwellSettings.ActivationButtonDwellMs;

    /// <summary>A seam for the work area's top, so a top-taskbar case can be laid out headless.</summary>
    internal Func<double> WorkAreaTopProvider { get; set; } = () => SystemParameters.WorkArea.Top;

    /// <summary>
    /// Shows the banner for a blocked state. The text names the elevated window when R1 has that
    /// evidence, and says only "input is being discarded" otherwise — R2, never R1, is the trigger.
    /// </summary>
    /// <param name="state">The transition that raised the banner.</param>
    public void ShowBlocked(BlockedInputState state)
    {
        var elevated = state.Reason is BlockedInputReason.HigherIntegrityForeground
            or BlockedInputReason.HigherIntegrityTarget;

        MessageText.Text = T(elevated ? "overlays.blocked.elevated" : "overlays.blocked.generic");

        // Name the process R1 actually blamed: the target's process when the target is the higher
        // one, otherwise the foreground's.
        var process = state.Reason == BlockedInputReason.HigherIntegrityTarget
            ? state.Target.ProcessName
            : state.Foreground.ProcessName;
        DetailText.Text = elevated && process is not null
            ? T("overlays.blocked.detail", process)
            : string.Empty;

        ApplyGeometry();
        _dwell.SetPauseGrace(_dwellSettings.DwellPauseGraceMs);
        _escapeOutline.SetProgress(0);
        _dismissOutline.SetProgress(0);
        _isShowing = true;

        if (_present is not null)
        {
            _present(this);
        }
        else
        {
            if (!IsVisible)
                Show();
            RaiseToTop();
        }

        UpdateLayout();
        PlaceDwellTargets();
    }

    /// <summary>Hides the banner and drops its dwell targets. Safe to call when not showing.</summary>
    public void HideBlocked()
    {
        if (!_isShowing && !IsVisible)
            return;

        _isShowing = false;
        _dwell.ClearTargets();
        _escapeOutline.SetProgress(0);
        _dismissOutline.SetProgress(0);
        if (IsVisible)
            Hide();

        // Only a real hide reaches here: the early return above makes a repeat call silent, so
        // the note gate's hide latch (C10 fixup-04) never fires for a banner that was not up.
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The WHOLE visible banner window in physical pixels while it is up (C10 fixup-04): the note
    /// gate's "blocked input banner" occluder. False when the banner is hidden, so a stale rect can
    /// never keep a note control gated after the strip is gone. Read per gaze sample on the UI
    /// thread, like every other occluder source.
    /// </summary>
    public bool TryGetBlockedVisibleBounds(out PhysicalRect bounds)
    {
        bounds = default;
        if (!_isShowing || !IsVisible)
            return false;

        // The window has no frame, so Left/Top/Width/Height ARE its screen rectangle in DIP; the
        // helper scales a coordinate and a length identically. The ticket's "whole window": the
        // message strip AND both emergency buttons.
        bounds = new PhysicalRect(
            DpiHelper.LogicalToPhysicalX(Left),
            DpiHelper.LogicalToPhysicalY(Top),
            DpiHelper.LogicalToPhysicalX(Width),
            DpiHelper.LogicalToPhysicalY(Height));
        return true;
    }

    /// <summary>Feeds the live gaze stream to the banner's dwell detector.</summary>
    /// <param name="gaze">The gaze point in physical pixels.</param>
    /// <param name="timestampMicroseconds">The gaze sample's timestamp.</param>
    public void OnGaze(PhysicalPoint gaze, long timestampMicroseconds)
    {
        if (_isShowing)
            _dwell.Update(gaze, timestampMicroseconds);
    }

    private void OnTargetActivated(string id)
    {
        if (id == DismissTargetId)
        {
            HideBlocked();
            DismissRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (id == EscapeTargetId)
        {
            EscapeRequested?.Invoke(this, EventArgs.Empty);
        }

        // Post-effect publish, like every dwell surface: the action runs first, then the cue.
        _messenger.Send(new DwellActivationMessage());
    }

    private void OnTargetProgress(string id, double progress)
    {
        if (id == EscapeTargetId)
            _escapeOutline.SetProgress(progress);
        else if (id == DismissTargetId)
            _dismissOutline.SetProgress(progress);
    }

    /// <summary>
    /// Places the button row below the message strip and (for a top-docked toolbar) below the
    /// toolbar's real band, then sizes the window to hold it. The same arithmetic the eye-care
    /// banner uses, so the two surfaces cannot disagree about where a top toolbar ends.
    /// </summary>
    private void ApplyGeometry()
    {
        ButtonLayer.Margin = new Thickness(
            0, ButtonsTopDip(), EyeCarePlacement.RingSideInsetDip, 0);
        Height = ButtonsTopDip() + ButtonHeightDip + ButtonBottomBandDip;
    }

    private double ButtonsTopDip() =>
        EyeCarePlacement.RingsTopDip(_toolbarSettings, WorkAreaTopProvider());

    /// <summary>
    /// Registers the two buttons' physical hit rectangles. A shown window gets its layout from the
    /// HWND-driven pass; the headless present seam cannot, so it arranges the content itself (the
    /// RestartNoticeWindow pattern). The rectangle is the WHOLE drawn button — for a gaze-only user
    /// aiming at a frozen cursor, a bigger honest target beats a tight circle.
    /// </summary>
    internal void PlaceDwellTargets()
    {
        var content = (FrameworkElement)Content;

        if (_present is not null)
        {
            content.Measure(new Size(Width, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, Width, Height));
        }

        var window = new Rect(Left, Top, Width, Height);
        _dwell.ClearTargets();
        _dwell.AddTarget(new DwellTarget(EscapeTargetId, BoundsOf(EscapeButton, content, window), DwellMs));
        _dwell.AddTarget(new DwellTarget(DismissTargetId, BoundsOf(DismissButton, content, window), DwellMs));
    }

    /// <summary>
    /// One element's screen rectangle in physical pixels: its bounds inside the window's content
    /// (the window has no frame, so the client origin is the window origin) plus the window's own
    /// screen position, scaled by the live DPI.
    /// </summary>
    /// <param name="element">The button.</param>
    /// <param name="contentRoot">The window's content element, the coordinate origin.</param>
    /// <param name="window">The window's rectangle in screen DIP.</param>
    /// <returns>The hit rectangle in physical screen pixels.</returns>
    internal static PhysicalRect BoundsOf(FrameworkElement element, FrameworkElement contentRoot, Rect window)
    {
        var inWindow = element.TransformToAncestor(contentRoot)
            .TransformBounds(new Rect(element.RenderSize));

        return new PhysicalRect(
            (window.Left + inWindow.Left) * DpiHelper.ScaleX,
            (window.Top + inWindow.Top) * DpiHelper.ScaleY,
            inWindow.Width * DpiHelper.ScaleX,
            inWindow.Height * DpiHelper.ScaleY);
    }

    private static string T(string key, params object?[] args) => StringTable.Current.T(key, args);

    /// <summary>
    /// Takes the label out of its button before <see cref="DwellOutlineOverlay.Attach"/> re-parents
    /// it into its progress overlay (the RestartNoticeWindow pattern). WPF refuses to add an element
    /// that still has a logical parent.
    /// </summary>
    private static UIElement Detach(Border container, UIElement content)
    {
        container.Child = null;
        return content;
    }
}
