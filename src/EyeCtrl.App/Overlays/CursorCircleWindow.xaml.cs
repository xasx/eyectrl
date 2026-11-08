namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using Serilog;

public partial class CursorCircleWindow : OverlayWindow, ICursorCircleOverlay, IRingQuantizationInstrument
{
    // UI Constants
    private const int MinimumCircleSize = 10; // Minimum allowed circle size in pixels

    public CursorCircleWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // keep the cursor circle out of the magnifier/preview capture
        InitializeComponent();
        // The ring starts in its FreeLook (quiet) look. No IsPinned notification fires for the
        // default state, so without this the window would keep WPF's full-opacity default until the
        // first transition — i.e. the ring would read as "pinned" on launch.
        Opacity = FreeLookQuietOpacity;

        _firedFlash = new FiredFlash(FiredFlashOuter, FiredFlashCore);
        // A ring hidden mid-flash comes back at rest: the flash belongs to the fire, never to the
        // next time the ring reappears.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _firedFlash.End();
        };
    }

    /// <summary>
    /// The ring is hidden whenever the backdrop, magnifier, action menu or edge bar is up, so a
    /// fallback re-raise of the ring cannot bury a surface the user is acting on. It therefore
    /// needs no process-wide order repair (ring-team review-01 (b), C9 F2 fixup-01).
    /// </summary>
    protected override bool NeedsOrderRepairAfterBandFallback => false;

    private readonly FiredFlash _firedFlash;

    /// <summary>
    /// One brief weight step confirming a dwell fired (Wave C C1): the ring's own two bands, wider
    /// (<see cref="FiredFlash.PeakStack"/>), so it turns bolder in both polarities and never less
    /// visible. Only a ring that is on screen flashes: a fire while the window is hidden is dropped,
    /// never queued for its reappearance. Touches neither the bands nor <see cref="Window.Opacity"/>,
    /// so the quiet and pinned looks and the per-band rest values are exactly as they were once the
    /// step ends.
    /// </summary>
    public void FlashFired()
    {
        if (!IsVisible) return;
        _firedFlash.Start();
    }

    /// <summary>True while the fired flash is showing.</summary>
    internal bool IsFiredFlashLit => _firedFlash.IsLit;

    /// <summary>True while the flash's end is scheduled.</summary>
    internal bool IsFiredFlashEndScheduled => _firedFlash.IsEndScheduled;

    /// <summary>The flash's scheduled length.</summary>
    internal TimeSpan FiredFlashInterval => _firedFlash.Interval;

    /// <summary>The flash's end timer (tests read its priority).</summary>
    internal System.Windows.Threading.DispatcherTimer FiredFlashTimer => _firedFlash.Timer;

    /// <summary>Ends the flash now: what the timer's tick does (a pumpless test cannot tick it).</summary>
    internal void EndFiredFlash() => _firedFlash.End();

    /// <summary>
    /// Window opacity in FreeLook: the quiet look. The ring follows gaze but reads as a faint hint
    /// rather than a mark to chase, so the user's attention stays on the target; the pin raises it to
    /// full opacity when a hold starts, which is the only time the ring is meant to be watched.
    /// </summary>
    internal const double FreeLookQuietOpacity = 0.6;

    // The two looks live here: Opacity is always one of the two constants. The ctor establishes
    // the quiet one (no pin notification fires for the default state); the pin is bold because
    // stillness is the "you're holding" signal, FreeLook is quiet so the ring never competes
    // with the target.
    private bool _isPinned;

    /// <summary>Raises the ring to its bold pinned look while a fixation is held at the settle centroid.</summary>
    public void SetPinned(bool pinned)
    {
        _isPinned = pinned;
        ApplyEmphasis();
    }

    private void ApplyEmphasis() =>
        Opacity = _isPinned ? 1.0 : FreeLookQuietOpacity;

    public void UpdatePosition(PhysicalPoint point) => ApplyPosition(point, isGlideFrame: false);

    /// <summary>
    /// Applies a pin-edge or glide-frame position. Glide frames are ordinary Left/Top writes — the
    /// interpolation itself lives in <c>CursorCircleViewModel</c> on the sample clock, deliberately
    /// without <c>BeginAnimation</c>, so a pump-less read observes the drawn value and no held
    /// animation outranks a later write — but they are NOT handed to instrument 2: its cadence
    /// histogram measures the ring's steady-state writes, and an animation's frames would dominate it.
    /// </summary>
    public void MoveTo(PhysicalPoint point, bool glide) => ApplyPosition(point, isGlideFrame: glide);

    private void ApplyPosition(PhysicalPoint point, bool isGlideFrame)
    {
        double left = DpiHelper.PhysicalToLogicalX(point.X) - Width / 2;
        double top = DpiHelper.PhysicalToLogicalY(point.Y) - Height / 2;

        Left = left;
        Top = top;

        // Instrument 2 (Stage 2), off unless an operator turned it on — and the setting is re-read per
        // write, so turning it off mid-session stops the work rather than waiting for a restart.
        // Strictly an observation of the two writes above: it reads back where the window actually
        // landed and reports it. Nothing about how the ring is positioned changes.
        if (!isGlideFrame && _quantizationProbe is not null && _diagnostics?.MeasureUiInstruments == true)
            RecordQuantization(left, top);
    }

    // ── Instrument 2: ring pixel quantization and cadence ──
    //
    // The question: sub-pixel filter output is produced all the way down the pipeline, but the ring is
    // moved by writing Window.Left/Top on a layered window. If the window manager lands it on whole
    // device pixels, every fractional pixel is discarded by the LAST stage and no filter — however well
    // tuned — can make the ring sub-pixel smooth. Answering it promotes or drops the transform-based
    // overlay endgame, so it is worth measuring rather than assuming.

    private RingQuantizationProbe? _quantizationProbe;
    private DiagnosticsSettings? _diagnostics;
    private long _quantizationSamplesSinceReport;

    /// <summary>Writes between summary log lines (~60 s at 33 Hz).</summary>
    private const long QuantizationReportInterval = 2000;

    /// <summary>
    /// Attaches the quantization instrument. Called at wiring time only when the diagnostics setting is
    /// on; leaving it unattached keeps <see cref="UpdatePosition"/> exactly as it was.
    /// </summary>
    /// <param name="probe">The accumulator to report observations to.</param>
    /// <param name="diagnostics">
    /// Settings carrying the live gate. Read per write so switching the setting off mid-session actually
    /// stops the measurement — the probe is attached once at startup, so without this "off" would only
    /// take effect at the next restart, which is not what the setting says. Switching it back ON
    /// mid-session deliberately does nothing (same discipline as the raw-gaze recorder).
    /// </param>
    public void AttachQuantizationProbe(RingQuantizationProbe probe, DiagnosticsSettings diagnostics)
    {
        _quantizationProbe = probe;
        _diagnostics = diagnostics;
    }

    /// <summary>Emits the instrument's verdict and cadence histogram. Called at shutdown.</summary>
    public void ReportQuantization()
    {
        if (_quantizationProbe is null || _quantizationProbe.Count == 0) return;
        Log.Information("UI instrument 2 (final) — {Verdict}", _quantizationProbe.Describe());
        Log.Information("UI instrument 2 (final) — {Cadence}", _quantizationProbe.DescribeCadence());
    }

    private void RecordQuantization(double requestedLeftDip, double requestedTopDip)
    {
        try
        {
            // GetWindowRect reports the ACTUAL placement in device pixels — a RECT is integral by
            // definition, which is precisely why the comparison has to happen in device-pixel space:
            // in DIPs on a 100 % display, one DIP is one device pixel and total quantization would be
            // indistinguishable from perfect fidelity.
            //
            // LOAD-BEARING ASSUMPTION, recorded because a wrong verdict here drives a roadmap decision:
            // this read must observe the Left/Top writes that just happened, i.e. WPF must apply HWND
            // bounds synchronously on the UI thread (setting Window.Left calls through to SetWindowPos
            // in the same call; only the layered window's CONTENT presents asynchronously). If that is
            // wrong and moves are deferred by a frame, `residual` stops measuring quantization and
            // starts measuring per-frame gaze motion — ExactMatchFraction then collapses on any live
            // gaze and the probe reports DISCARDED from lag alone.
            //
            // It could not be verified here: it needs a sourced window, so no headless test can settle
            // it. PRECONDITION before a DISCARDED verdict is allowed to promote the transform-based
            // overlay endgame: park the ring at a deliberately fractional STATIC position (gaze still,
            // or a fixed point) and confirm the residual stays fractional-but-constant. Under lag a
            // static position reads as a perfect match, which distinguishes the two causes outright.
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;          // not yet sourced; nothing to observe
            if (!GetWindowRect(handle, out var rect)) return;

            _quantizationProbe!.Record(
                requestedLeftDip, requestedTopDip,
                rect.Left, rect.Top,
                DpiHelper.ScaleX, DpiHelper.ScaleY);

            if (++_quantizationSamplesSinceReport >= QuantizationReportInterval)
            {
                _quantizationSamplesSinceReport = 0;
                Log.Information("UI instrument 2 — {Verdict}", _quantizationProbe.Describe());
                Log.Information("UI instrument 2 — {Cadence}", _quantizationProbe.DescribeCadence());
            }
        }
        catch (Exception ex)
        {
            // A diagnostic must never be able to break the cursor ring, which is the user's only
            // pointer feedback. Detach on the first fault rather than throwing once per sample.
            _quantizationProbe = null;
            Log.Warning(ex, "Ring quantization instrument detached after a fault; the ring is unaffected");
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public void UpdateAppearance(CursorSettings settings)
    {
        var size = Math.Max(MinimumCircleSize, settings.CircleSize);

        // Core owns the geometry; this method owns only WPF. The palette colours are passed
        // in as hex because EyeCtrl.Core cannot reference EyeCtrl.Controls.
        _restStack = CursorRingStacks.For(
            settings.RingStyle, size, settings.CircleThickness, settings.CircleOpacity,
            settings.CircleColor, ToHex(Tokens.StrokeHalo), ToHex(Tokens.TextPrimary));
        _size = size;
        var stack = _restStack.Value;
        ApplyStack();

        // R2: the fill's bands are the same stack, heavier; rebuilt here with the rest look so a
        // settings change and a live fill can never disagree about colours or widths.
        RebuildFill(stack);

        Width = size;
        Height = size;
        Circle.Width = size;
        Circle.Height = size;
        CircleHalo.Width = size;
        CircleHalo.Height = size;

        // The fired flash's peak: the same two bands, same colours and opacities, only wider. Built
        // here, with the rest look, so a flash itself writes nothing but visibility.
        var peak = FiredFlash.PeakStack(stack, size);
        Apply(FiredFlashOuter, peak.Outer, Tokens.BrushStrokeHalo);
        Apply(FiredFlashCore, peak.Core, Tokens.BrushAccent);
        FiredFlashOuter.Width = size;
        FiredFlashOuter.Height = size;
        FiredFlashCore.Width = size;
        FiredFlashCore.Height = size;
    }

    /// <summary>The last settings-driven (rest) look, the base the blocked look derives from.</summary>
    private CursorRingStack? _restStack;

    private double _size;

    private bool _blocked;

    /// <summary>
    /// C10's blocked look: the two bands turn dashed while the detector reports that Windows is
    /// discarding EyeCtrl's injected input. The geometry comes from
    /// <see cref="CursorRingStacks.Blocked"/> — paired angular dashes for the solid styles, and
    /// BiTonal's interleave kept — so this method paints, never invents. Deliberately NOT dimmed:
    /// C1's lesson is that the owner's ring must never get harder to see, and
    /// <see cref="Window.Opacity"/> stays the pin/quiet channel. The rest look is re-applied on
    /// unblock, so a settings change and a block cannot leave each other's pixels behind.
    /// </summary>
    /// <param name="blocked">True while the detector reports a block. The look follows the detector,
    /// not the banner: a dismissed banner keeps the dashed cue.</param>
    public void SetBlocked(bool blocked)
    {
        if (_blocked == blocked) return;
        _blocked = blocked;
        ApplyStack();
    }

    /// <summary>True while the blocked look is showing (test seam).</summary>
    internal bool IsBlockedLookOn => _blocked;

    private void ApplyStack()
    {
        if (_restStack is not { } rest) return;

        var stack = _blocked ? CursorRingStacks.Blocked(rest, _size) : rest;
        Apply(CircleHalo, stack.Outer, Tokens.BrushStrokeHalo);
        Apply(Circle, stack.Core, Tokens.BrushAccent);
    }

    // ── Rest activation R2: the fill ──
    //
    // The arc is the ring's OWN stack, heavier (FiredFlash.PeakStack: same colours and opacities,
    // only wider), from 12 o'clock clockwise at FillProgress × 360°. It is built from the REST
    // stack, like C1's fired flash, and is drawn solid: the ring's style dashes (BiTonal's, and
    // C10's blocked look) stay on the ring's own bands, which the fill never repaints. The fill is
    // a progress mark whose leading edge must be locatable; the unfilled part of the ring keeps
    // every cue. No colour of its own, no wash, no disc — the two bands keep the two polarities.

    private double _fillProgress;
    private bool _fillPaused;
    private CursorRingStack? _fillPeak;
    private double _fillCentre;
    private FillArc? _fillOuter;
    private FillArc? _fillCore;

    /// <summary>One band's fill geometry: the reusable partial arc (mutated per frame, never
    /// reallocated) and the cached full ring for progress 1.</summary>
    private sealed record FillArc(PathGeometry Geometry, ArcSegment Segment, double Radius, Geometry Full);

    /// <summary>
    /// The rest fill from the ring's view model: the arc's fraction (0..1) and whether the fill is
    /// held in its grace. Display only; 0 collapses the arc, and a non-finite value is treated as 0.
    /// </summary>
    public void UpdateFill(double progress, bool paused)
    {
        _fillProgress = double.IsFinite(progress) ? Math.Clamp(progress, 0.0, 1.0) : 0.0;
        _fillPaused = paused;
        ApplyFill();
    }

    /// <summary>Rebuilds the fill's bands and geometry for a new ring size/thickness/colour. The
    /// current progress is re-applied, so a settings change mid-fill never strands a stale arc.</summary>
    private void RebuildFill(CursorRingStack rest)
    {
        var peak = FiredFlash.PeakStack(rest, _size);
        _fillPeak = peak;
        _fillCentre = _size / 2.0;
        _fillOuter = BuildFillArc(_size, peak.Outer.Thickness);
        _fillCore = BuildFillArc(_size, peak.Core.Thickness);

        Apply(FillArcOuter, peak.Outer, Tokens.BrushStrokeHalo);
        Apply(FillArcCore, peak.Core, Tokens.BrushAccent);

        // Solid in every style (see the section comment above): clear the band's dash geometry that
        // Apply copied. Both bands stay the ring's colours, opacities and (heavier) widths.
        FillArcOuter.StrokeDashArray = new DoubleCollection();
        FillArcOuter.StrokeDashOffset = 0;
        FillArcCore.StrokeDashArray = new DoubleCollection();
        FillArcCore.StrokeDashOffset = 0;

        ApplyFill();
    }

    /// <summary>
    /// One band's arc: a path whose stroke is centred on <c>(size − thickness) / 2</c>, so its
    /// OUTER edge lands on the ring's outer edge exactly as the ellipse bands do. A Path strokes
    /// centred while a Shape's ellipse strokes inside its bounds; the radius formula is what makes
    /// the two coincide. Reusing <see cref="DwellArcButton.UpdateArc"/> keeps the per-frame path
    /// allocation-free: the figure and segment are built once and only their end point moves.
    /// </summary>
    private FillArc BuildFillArc(double size, double thickness)
    {
        double radius = Math.Max(0, (size - thickness) / 2);
        var start = new Point(_fillCentre, _fillCentre - radius);
        var segment = new ArcSegment
        {
            Point = start,
            Size = new Size(radius, radius),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise,
        };
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(segment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        // ArcSegment cannot draw a closed circle (start == end is degenerate), so the full ring is
        // its own cached, frozen ellipse with the same centre line and stroke.
        var full = new EllipseGeometry(new Point(_fillCentre, _fillCentre), radius, radius);
        full.Freeze();

        return new FillArc(geometry, segment, radius, full);
    }

    private void ApplyFill()
    {
        if (_fillPeak is not { } peak || _fillOuter is not { } outer || _fillCore is not { } core
            || _fillProgress <= 0)
        {
            FillArcOuter.Visibility = Visibility.Collapsed;
            FillArcCore.Visibility = Visibility.Collapsed;
            return;
        }

        // Paused is the shared dwell-paused cue's opacity (0.7), applied to BOTH bands: measured at
        // ≥ 3:1 on white and black for the owner's AchromaticReticle, and still plainly visible.
        double pausedOpacity = DwellPausedCue.OpacityFor(_fillPaused);
        FillArcOuter.Opacity = peak.Outer.Opacity * pausedOpacity;
        FillArcCore.Opacity = peak.Core.Opacity * pausedOpacity;

        if (_fillProgress >= 1)
        {
            FillArcOuter.Data = outer.Full;
            FillArcCore.Data = core.Full;
        }
        else
        {
            FillArcOuter.Data = outer.Geometry;
            FillArcCore.Data = core.Geometry;
            DwellArcButton.UpdateArc(outer.Segment, new Point(_fillCentre, _fillCentre), outer.Radius, _fillProgress);
            DwellArcButton.UpdateArc(core.Segment, new Point(_fillCentre, _fillCentre), core.Radius, _fillProgress);
        }

        FillArcOuter.Visibility = Visibility.Visible;
        FillArcCore.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Paints one band onto one ellipse. The brush is built and frozen here, once per
    /// settings change and never per frame — see the note in HaloZOrderTests about why this
    /// file is exempt from ActivationButton_NeverAllocatesABrush.
    /// </summary>
    /// <param name="target">The ellipse to paint.</param>
    /// <param name="band">The band's colour, width, opacity and dash geometry.</param>
    /// <param name="fallback">Used when the band's hex is unparseable, which only the
    /// user-supplied mark colour can be.</param>
    private static void Apply(Shape target, RingBand band, SolidColorBrush fallback)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(band.Hex));
            brush.Freeze();
            target.Stroke = brush;
        }
        catch (Exception ex)
        {
            // Deliberately broad rather than narrowed to FormatException (the only failure
            // reachable here for string input on .NET 10 — ConvertFromString always boxes a
            // Color on success for a string argument, so the cast cannot throw). Kept broad on
            // two grounds: it preserves the breadth that already shipped on main — narrowing
            // would itself be a behaviour change — and WPF's colour-parser internals are
            // undocumented, so breadth is cheap insurance in a method that runs on the
            // dispatcher, where App.xaml.cs's DispatcherUnhandledException handler deliberately
            // does not set Handled and an escaped exception therefore kills the process, taking
            // this user's only input channel with it.
            Log.Warning(ex, "Could not parse cursor ring colour {Hex}; using the theme brush", band.Hex);
            target.Stroke = fallback;
        }

        target.StrokeThickness = band.Thickness;
        target.Opacity = band.Opacity;

        if (band.DashPeriod > 0)
        {
            // StrokeDashArray and StrokeDashOffset are both in MULTIPLES OF StrokeThickness,
            // not device pixels. Dash and gap are equal, and the offset is one dash — half a
            // cycle — which interleaves the two bands.
            var units = band.DashPeriod / band.Thickness;
            target.StrokeDashArray = new DoubleCollection { units, units };
            target.StrokeDashOffset = band.DashOffset / band.Thickness;
        }
        else
        {
            target.StrokeDashArray = new DoubleCollection();
            target.StrokeDashOffset = 0;
        }
    }

    /// <summary>
    /// A colour as a stored hex string. Explicitly invariant, built without a format hole: a hex
    /// code is data, never a localized number (T02 fixup-01 F4 — the widened specifier gate flags
    /// X2 like any other numeric specifier).
    /// </summary>
    private static string ToHex(Color c) =>
        "#" + c.R.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
            + c.G.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
            + c.B.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
}
