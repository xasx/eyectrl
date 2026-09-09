namespace EyeCtrl.Core.Models;

/// <summary>
/// A complete description of the display's addressable space at one instant: the primary screen's
/// bounds in physical pixels and the logical-to-physical scale factors.
/// </summary>
/// <remarks>
/// <b>Why this is one type rather than four numbers.</b> Three separate caches used to hold this
/// information — the DPI scale, the gaze provider's bounds that normalised 0..1 gaze is multiplied
/// by, and the toolbar's screen size plus summon rectangle — and each was filled once at startup
/// and never refreshed. Nothing in the application listened for a display change at all, so a dock,
/// undock, resolution change, scaling change or RDP reconnect left gaze addressing the wrong pixel
/// space with dwell targets displaced from where they were drawn.
/// <para>
/// Refreshing them independently would have replaced that with a subtler version of the same
/// failure: the toolbar's summon region — the escape hatch a user reaches for when anything else
/// goes wrong — could disagree with the coordinate space gaze arrives in. So the refresh is
/// computed once as this immutable snapshot and handed to each sink, and every sink publishes its
/// own copy by a single reference swap rather than field-by-field.
/// </para>
/// <para>
/// Being immutable is the load-bearing property: a reader either sees the whole previous geometry
/// or the whole new one, never a mixture. The pattern mirrors
/// <c>CalibrationService.CurrentTransform</c>, which publishes an immutable transform the same way
/// for the same reason.
/// </para>
/// </remarks>
/// <param name="WidthPx">Primary screen width in physical pixels.</param>
/// <param name="HeightPx">Primary screen height in physical pixels.</param>
/// <param name="ScaleX">Horizontal logical-to-physical scale (2.0 at 200% DPI).</param>
/// <param name="ScaleY">Vertical logical-to-physical scale.</param>
public sealed record DisplayGeometry(double WidthPx, double HeightPx, double ScaleX, double ScaleY)
{
    /// <summary>The identity geometry: unknown bounds at 100% scale. Used before the first read.</summary>
    public static readonly DisplayGeometry Unscaled = new(0, 0, 1.0, 1.0);

    /// <summary>
    /// True when this geometry describes a usable screen. Guards against acting on a transient
    /// zero-sized read: Windows reports 0x0 bounds briefly during a mode switch and while all
    /// monitors are detached, and applying that would scale every gaze sample to the origin.
    /// </summary>
    public bool IsUsable => WidthPx > 0 && HeightPx > 0 && ScaleX > 0 && ScaleY > 0;

    /// <summary>
    /// Whether <paramref name="other"/> differs enough to be worth applying. Display-change
    /// notifications arrive in bursts and often report identical geometry, and re-applying is not
    /// free: it repositions overlays and republishes dwell targets. The epsilon absorbs
    /// floating-point noise in the scale factors rather than any real tolerance for drift.
    /// </summary>
    public bool DiffersFrom(DisplayGeometry other) =>
        Math.Abs(WidthPx - other.WidthPx) > 0.5
        || Math.Abs(HeightPx - other.HeightPx) > 0.5
        || Math.Abs(ScaleX - other.ScaleX) > 1e-6
        || Math.Abs(ScaleY - other.ScaleY) > 1e-6;
}
