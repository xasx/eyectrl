namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Shapes;
using System.Windows.Threading;
using EyeCtrl.Core.Models;

/// <summary>
/// The "fired" flash (Wave C ticket C1): one brief WEIGHT step on a ring, confirming that a dwell
/// fired. The ring turns bolder for <see cref="DurationMs"/> and then returns to rest.
/// <para>
/// Polarity-keeping by design (C1 fixup-01, ring review F1): the step redraws the ring's own two
/// bands, in their own colours and opacities, only wider. A colour swap would collapse the
/// two-polarity reticle into one polarity, and on light content the owner's only visible feature
/// (the thin dark core) would take the page colour: the pointer would vanish for the step. A wider
/// copy of both bands keeps every edge the rest look has, so the ring is never less visible at the
/// peak than at rest, on any backdrop.
/// </para>
/// <para>
/// Drives two overlay ellipses declared LAST (so they paint over the rest bands), collapsed at
/// rest. Nothing the rest look is made of is ever written: not the bands (pinned by
/// CursorRingStacksTests/CursorRingRenderTests), and never Window.Opacity, which carries the
/// quiet/pinned look and must stay exactly 0.6 or 1.0. Returning to rest is therefore one
/// Visibility write per overlay, and cannot leave a band half-restored. The overlays' geometry and
/// brushes are the owning window's job (<see cref="PeakStack"/> for the cursor ring).
/// </para>
/// <para>
/// A hard step rather than a fade: a fade spends most of its 120 ms near the rest look, which is
/// exactly the part of the flash that is hard to see. The end runs on a UI-thread
/// <see cref="DispatcherTimer"/> at <see cref="DispatcherPriority.Render"/>, never on the gaze
/// thread: at Render the end tick stays behind the render the Visibility write queued, so a UI stall
/// cannot collapse the step before a frame drew it (review 01 F2). <see cref="End"/> is the test
/// seam too: a pumpless STA test never ticks a DispatcherTimer (the ToolbarActivationFlashTests
/// reason).
/// </para>
/// </summary>
internal sealed class FiredFlash
{
    /// <summary>
    /// How long the step is lit. The ticket's bound is 150 ms for the whole flash; 120 leaves room
    /// for the DispatcherTimer's tick granularity inside it.
    /// </summary>
    internal const int DurationMs = 120;

    /// <summary>The core band's width at the peak, as a multiple of its rest width.</summary>
    internal const double PeakCoreFactor = 3.0;

    /// <summary>
    /// The outer band's EXPOSED width at the peak (the part the core does not cover), as a multiple
    /// of its rest exposed width.
    /// </summary>
    internal const double PeakOuterExposedFactor = 2.0;

    /// <summary>The end timer's priority (fixup-01 R5); a DispatcherTimer does not expose its own.</summary>
    internal const DispatcherPriority EndPriority = DispatcherPriority.Render;

    private readonly Shape[] _bands;
    private readonly DispatcherTimer _timer;

    /// <param name="bands">The overlay ellipses, in paint order. Only their visibility is written.</param>
    internal FiredFlash(params Shape[] bands)
    {
        _bands = bands;
        _timer = new DispatcherTimer(EndPriority, bands[0].Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(DurationMs),
        };
        _timer.Tick += (_, _) => End();
        SetVisibility(Visibility.Collapsed);
    }

    /// <summary>True while the step is showing.</summary>
    internal bool IsLit => _bands[0].Visibility == Visibility.Visible;

    /// <summary>True while the end is scheduled; false at rest.</summary>
    internal bool IsEndScheduled => _timer.IsEnabled;

    /// <summary>The end timer itself, for tests that read its priority (DispatcherTimer has no public getter).</summary>
    internal DispatcherTimer Timer => _timer;

    /// <summary>The scheduled length of the step.</summary>
    internal TimeSpan Interval => _timer.Interval;

    /// <summary>
    /// Lights the step and (re)schedules its end. A second fire during a live step restarts the
    /// timer, so the flash never outlasts <see cref="DurationMs"/> from the latest fire and never
    /// stacks into a longer one.
    /// </summary>
    internal void Start()
    {
        SetVisibility(Visibility.Visible);
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Returns to the rest look at once. Idempotent.</summary>
    internal void End()
    {
        _timer.Stop();
        SetVisibility(Visibility.Collapsed);
    }

    private void SetVisibility(Visibility visibility)
    {
        foreach (var band in _bands)
            band.Visibility = visibility;
    }

    /// <summary>
    /// The peak of the weight step for a rest stack: the SAME two bands (colour, opacity, dash
    /// geometry), only wider. Both bands share the ring's outer edge (WPF draws an ellipse's stroke
    /// inside its bounds), so the core covers the outer band's first <c>core</c> DIP and the outer
    /// band shows only past it. At the peak the core is <see cref="PeakCoreFactor"/>× as wide and the
    /// outer band's exposed part is <see cref="PeakOuterExposedFactor"/>× as wide: for the owner's
    /// AchromaticReticle at 1.5 that is a 1.5 → 4.5 DIP dark line and a 4 → 8 DIP light band.
    /// BiTonalSegments (equal widths, interleaved dashes) triples both.
    /// <para>
    /// Half the ring's size is the room a stroke has (it then fills the disc). On a small, thick
    /// ring the growth is shared out inside that room so that NEITHER band ends up thinner than at
    /// rest (ring review r02 N1): the core grows only as far as the room left after the outer
    /// band's grown exposed part, and never shrinks below its rest width; the outer band's exposed
    /// part then takes what remains, and never shrinks below ITS rest width. Capping the core alone
    /// let it reach the outer band's width on e.g. a 30/5 ring, which hid the light band at the
    /// peak (Midnight on black: 5.69 → 1.04:1). A ring whose rest stroke already overflows the room
    /// keeps its rest widths at the peak.
    /// </para>
    /// </summary>
    internal static CursorRingStack PeakStack(CursorRingStack rest, double size)
    {
        var room = size / 2;
        var exposed = Math.Max(0, rest.Outer.Thickness - rest.Core.Thickness);
        var grownExposed = exposed * PeakOuterExposedFactor;
        var core = Math.Min(rest.Core.Thickness * PeakCoreFactor,
            Math.Max(rest.Core.Thickness, room - grownExposed));
        var peakExposed = Math.Max(exposed, Math.Min(grownExposed, room - core));
        return new CursorRingStack(
            rest.Outer with { Thickness = core + peakExposed },
            rest.Core with { Thickness = core });
    }
}
