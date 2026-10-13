namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
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
        // The ring starts in its FreeLook (quiet) look. No IsPinned/IsPaused notification fires for the
        // default state, so without this the window would keep WPF's full-opacity default until the
        // first transition — i.e. the ring would read as "pinned" on launch.
        Opacity = FreeLookQuietOpacity;
    }

    /// <summary>
    /// Window opacity for the paused state. Defensive only: the VM hides the ring while paused and
    /// no production path shows the window in that state — this is the look a future direct show
    /// while paused must present, never the quiet FreeLook ring.
    /// </summary>
    internal const double PausedDimOpacity = 0.45;

    /// <summary>
    /// Window opacity in FreeLook: the quiet look. The ring follows gaze but reads as a faint hint
    /// rather than a mark to chase, so the user's attention stays on the target; the pin raises it to
    /// full opacity when a hold starts, which is the only time the ring is meant to be watched.
    /// Deliberately above <see cref="PausedDimOpacity"/> so the three looks stay distinguishable.
    /// </summary>
    internal const double FreeLookQuietOpacity = 0.6;

    // The three looks live here, one owner of Opacity. Precedence is explicit: Paused beats pinned
    // beats quiet. Paused is the dimmest because the resume target must win the user's attention;
    // pinned is bold because stillness is the "you're holding" signal; FreeLook is quiet.
    private bool _isPaused;
    private bool _isPinned;

    /// <summary>Dims the ring while interaction is paused; the quiet FreeLook look returns on resume.</summary>
    public void SetPausedDim(bool dimmed)
    {
        _isPaused = dimmed;
        ApplyEmphasis();
    }

    /// <summary>Raises the ring to its bold pinned look while a fixation is held at the settle centroid.</summary>
    public void SetPinned(bool pinned)
    {
        _isPinned = pinned;
        ApplyEmphasis();
    }

    private void ApplyEmphasis() =>
        Opacity = _isPaused ? PausedDimOpacity : _isPinned ? 1.0 : FreeLookQuietOpacity;

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
        var stack = CursorRingStacks.For(
            settings.RingStyle, size, settings.CircleThickness, settings.CircleOpacity,
            settings.CircleColor, ToHex(Tokens.StrokeHalo), ToHex(Tokens.TextPrimary));

        Apply(CircleHalo, stack.Outer, Tokens.BrushStrokeHalo);
        Apply(Circle, stack.Core, Tokens.BrushAccent);

        Width = size;
        Height = size;
        Circle.Width = size;
        Circle.Height = size;
        CircleHalo.Width = size;
        CircleHalo.Height = size;
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
    private static void Apply(Ellipse target, RingBand band, SolidColorBrush fallback)
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
