namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class EyeCareBannerWindow : OverlayWindow, IEyeCareBanner
{
    /// <summary>
    /// The dwell ring's diameter in DIP, declared in XAML as the two rings' Grid Width/Height.
    /// B7 raised it from 64 to 100 so the drawn circle IS the r50 hit area (R4's size floor):
    /// the circle the user aims at is the circle gaze is tested against.
    ///
    /// Class-level and <c>internal</c> rather than a local inside the constructor: as a method
    /// local it was unreachable from any test, so nothing tied it to the XAML attribute or
    /// recorded which ratio the glyphs inside it take. <c>GlyphSizeCouplingTests</c> now asserts
    /// both.
    /// </summary>
    internal const double RingSize = 100;

    /// <summary>The dwell target's hit radius in DIP — half the drawn disc (R4's ≥ 50 floor).</summary>
    internal const double HitRadiusDip = RingSize / 2;

    /// <summary>
    /// Centre-to-centre distance between the two rings: the drawn diameter plus a 24 DIP gap.
    /// R4 asks for a dead gap ≥ 20 between different actions; 24 leaves 2 DIP of slack each side
    /// for the progress stroke, which grows outward by up to 2 DIP at full dwell.
    /// </summary>
    internal const double RingPitchDip = RingSize + 24;

    /// <summary>DIP from the screen's right edge to the Start disc's drawn edge.</summary>
    internal const double RightInsetDip = 20;

    /// <summary>DIP between the toolbar band's bottom and the rings' top edge.</summary>
    internal const double RingTopGapDip = 8;

    /// <summary>
    /// The toolbar band's height above the rings: <c>RepeatModeController.BuildToolbarBandRect</c>'s
    /// own formula (toolbar size + 40). Duplicated rather than referenced because that band is a
    /// repeat-mode suppression zone, not a placement primitive; <c>EyeCareBannerGeometryTests</c>
    /// pins the rings clear of the real band rect at DPI 1 and 2, so a drift fails there.
    /// </summary>
    internal const double ToolbarBandExtraDip = 40;

    /// <summary>Room below the discs for their captions (16 DIP offset + one body line + padding).</summary>
    internal const double CaptionBandDip = 44;

    /// <summary>The ring stroke at rest and at full dwell (DIP); the difference is the visible
    /// progress, alongside the opacity ramp.</summary>
    internal const double RingStrokeRest = 2;
    internal const double RingStrokeFull = 6;
    private const double RingOpacityRest = 0.7;

    private readonly DwellDetector _dwell;
    private readonly int _dwellMs;
    private readonly DwellSettings _dwellSettings;
    private readonly ToolbarSettings _toolbarSettings;
    // Dwell-completion cue trigger. Optional-trailing so no caller changes; the fallback IS the
    // app's registered singleton (AppBootstrapper registers WeakReferenceMessenger.Default as
    // IMessenger — spec D9), so the production factory's default path publishes on the same bus
    // the DwellCueService subscribes to.
    private readonly IMessenger _messenger;
    private readonly Dictionary<string, Ellipse> _rings;
    private PhysicalPoint _startCenter = new(0, 0);
    private PhysicalPoint _snoozeCenter = new(0, 0);
    private DispatcherTimer? _autoSnooze;
    private TaskCompletionSource<bool>? _tcs;

    // Reuse the app's activation dwell time so the buttons feel consistent.
    public EyeCareBannerWindow(int dwellMs = 700, IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null, IMessenger? messenger = null,
        DwellSettings? dwellSettings = null, ToolbarSettings? toolbarSettings = null)
    {
        _dwellSettings = dwellSettings ?? new DwellSettings();
        _toolbarSettings = toolbarSettings ?? new ToolbarSettings();
        InitializeComponent();

        // Ring glyphs are a ratio of the 100px dwell ring that bounds them (spec §8.5). Assigned
        // here because XAML cannot multiply, and a plain assignment needs no allow-list entry.
        SnoozeGlyph.FontSize = RingSize * ContainerRatio.Medium;
        StartGlyph.FontSize = RingSize * ContainerRatio.Medium;

        IsClickThrough = true; // set here (read in OverlayWindow.OnSourceInitialized), NOT in XAML
        _dwellMs = dwellMs;
        _messenger = messenger ?? WeakReferenceMessenger.Default;

        // A1's forgiving dwell (the same grace every overlay surface carries): a blink mid-dwell
        // holds the progress instead of restarting it. Re-applied at every ShowAsync, because the
        // window is a startup singleton and the settings object is edited live.
        _dwell = new DwellDetector(cooldownMs: 0, context: DwellContext.EyeCare, sink: sink,
            pauseGraceMs: _dwellSettings.DwellPauseGraceMs)
        {
            OtherTargetReleasesHold = true
        };
        _dwell.TargetActivated += OnDwellActivated;
        _dwell.TargetProgress += OnTargetProgress;
        dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            _dwell, DwellContext.EyeCare,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs));

        _rings = new Dictionary<string, Ellipse>(StringComparer.Ordinal)
        {
            ["snooze"] = SnoozeRing,
            ["start"] = StartRing,
        };
        ResetRingProgress();

        // Place the rings at construction too: the layout fitters and the geometry tests lay the
        // content out without ever calling ShowAsync.
        ApplyGeometry();
    }

    /// <summary>The detector — the test seam for the rings' grace and their progress.</summary>
    internal DwellDetector DwellForTest => _dwell;

    /// <summary>
    /// The two dwell circles' centres and hit radius in physical pixels. The rings are inset from
    /// the TOP by the toolbar band (so they never sit on the top edge where tracking is worst) and
    /// from the RIGHT by <see cref="RightInsetDip"/>; the Start disc is the right-hand one. Pure,
    /// so the headless tests pin DPI 1 and 2 without a display.
    /// </summary>
    /// <param name="screenDipWidth">The screen's width in DIP.</param>
    /// <param name="toolbarSizeDip">The docked toolbar's button size in DIP — the band it draws
    /// above the rings is <c>toolbarSizeDip + 40</c>.</param>
    /// <param name="scaleX">Physical pixels per DIP, horizontally.</param>
    /// <param name="scaleY">Physical pixels per DIP, vertically.</param>
    /// <returns>The Start and Snooze centres (physical px) and the hit radius (physical px).</returns>
    public static (PhysicalPoint Start, PhysicalPoint Snooze, double Radius) ComputeDwellGeometry(
        double screenDipWidth, double toolbarSizeDip, double scaleX, double scaleY)
    {
        var screenPhysWidth = screenDipWidth * scaleX;
        var physTop = (RingTopDip(toolbarSizeDip) + HitRadiusDip) * scaleY;
        var start = new PhysicalPoint(
            screenPhysWidth - (RightInsetDip + HitRadiusDip) * scaleX, physTop);
        var snooze = new PhysicalPoint(start.X - RingPitchDip * scaleX, physTop);
        return (start, snooze, HitRadiusDip * scaleX);
    }

    /// <summary>The rings' top edge in DIP: clear of the toolbar band, plus a small gap.</summary>
    internal static double RingTopDip(double toolbarSizeDip) =>
        toolbarSizeDip + ToolbarBandExtraDip + RingTopGapDip;

    /// <summary>The window height the rings and their captions need, in DIP.</summary>
    internal static double WindowHeightDip(double toolbarSizeDip) =>
        RingTopDip(toolbarSizeDip) + RingSize + CaptionBandDip;

    /// <summary>
    /// Places both discs for the CURRENT toolbar size (the panel's top margin, below the toolbar
    /// band, and the window height around the captions) and registers nothing — a target is added
    /// only while showing. The horizontal placement is the XAML's right-anchored panel, whose
    /// <see cref="RightInsetDip"/> margin and 100 + 24 DIP boxes are the same constants
    /// <see cref="ComputeDwellGeometry"/> uses.
    /// </summary>
    private void ApplyGeometry()
    {
        var toolbarSize = _toolbarSettings.Size;
        RingLayer.Margin = new Thickness(0, RingTopDip(toolbarSize), RightInsetDip, 0);
        Height = WindowHeightDip(toolbarSize);

        var screenWidth = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        var geometry = ComputeDwellGeometry(
            screenWidth, toolbarSize, DpiHelper.ScaleX, DpiHelper.ScaleY);
        _startCenter = geometry.Start;
        _snoozeCenter = geometry.Snooze;
    }

    /// <summary>Shows the banner and resolves to true=accept, false=snooze/auto-dismiss.</summary>
    public Task<bool> ShowAsync(string message, PhysicalPoint screenCenter, int autoSnoozeSeconds)
    {
        MessageText.Text = $"👁 {message}";

        // The window is a startup singleton: re-read the live toolbar size and grace here, at
        // session entry, so a Settings change does not wait for a restart. ActualWidth and
        // SystemParameters.PrimaryScreenWidth are DIPs; DwellTarget expects physical pixels.
        ApplyGeometry();
        _dwell.SetPauseGrace(_dwellSettings.DwellPauseGraceMs);

        var radius = HitRadiusDip * DpiHelper.ScaleX;
        _dwell.ClearTargets();
        _dwell.AddTarget(new DwellTarget("start", _startCenter, radius, dwellTimeMs: _dwellMs));
        _dwell.AddTarget(new DwellTarget("snooze", _snoozeCenter, radius, dwellTimeMs: _dwellMs));
        ResetRingProgress();

        WorkDim.Visibility = Visibility.Visible;
        Show();
        // The offer coordinator suspends offers while the banner is up (ticket 8 fixup, review B F3).
        _messenger.Send(new EyeCareBannerActiveMessage(true));

        _tcs = new TaskCompletionSource<bool>();
        _autoSnooze = new DispatcherTimer(
            TimeSpan.FromSeconds(autoSnoozeSeconds), DispatcherPriority.Normal,
            (_, _) => Resolve(false), Dispatcher);
        _autoSnooze.Start();
        return _tcs.Task;
    }

    /// <summary>Feeds gaze to the dwell detector (called by EyeCareHooks from the gaze stream).</summary>
    public void OnGaze(PhysicalPoint gaze, long timestampUs) => _dwell.Update(gaze, timestampUs);

    /// <summary>Force-dismisses the banner (e.g. eye-care toggled off). Resolves as not-accepted.
    /// No-op when nothing is showing.</summary>
    public void Cancel()
    {
        if (_tcs is not null) Resolve(false);
    }

    // Post-effect publish (spec D8): the handler runs first — Resolve dismisses the banner —
    // then the cue fires, confirming what the user just heard as an activation. The wrapper
    // exists only for that post-effect send — the method group it replaced
    // (+= OnTargetActivated) was already unsubscribable by name.
    private void OnDwellActivated(string id)
    {
        OnTargetActivated(id);
        _messenger.Send(new DwellActivationMessage());
    }

    private void OnTargetActivated(string id) => Resolve(id == "start");

    /// <summary>
    /// Visible dwell progress: both rings thicken from <see cref="RingStrokeRest"/> to
    /// <see cref="RingStrokeFull"/> and brighten toward full opacity. The feedback moved off the
    /// fill for the same reason DwellOutlineOverlay exists: a fill wash would sit under the glyph.
    /// </summary>
    private void OnTargetProgress(string id, double progress)
    {
        if (!_rings.TryGetValue(id, out var ring)) return;
        var p = Math.Clamp(progress, 0, 1);
        ring.StrokeThickness = RingStrokeRest + (RingStrokeFull - RingStrokeRest) * p;
        ring.Opacity = RingOpacityRest + (1 - RingOpacityRest) * p;
    }

    private void ResetRingProgress()
    {
        foreach (var ring in _rings.Values)
        {
            ring.StrokeThickness = RingStrokeRest;
            ring.Opacity = RingOpacityRest;
        }
    }

    private void Resolve(bool accepted)
    {
        _autoSnooze?.Stop();
        _autoSnooze = null;
        _dwell.ClearTargets();
        ResetRingProgress();
        Hide();
        WorkDim.Visibility = Visibility.Collapsed;
        // Offers may resume. Resolve can run before the banner ever showed (Cancel on a never-shown
        // banner), so the message is sent unconditionally — the coordinator's flag is idempotent.
        _messenger.Send(new EyeCareBannerActiveMessage(false));
        _tcs?.TrySetResult(accepted);
        _tcs = null;
    }
}
