namespace EyeCtrl.Controls;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;

/// <summary>
/// Dwell feedback as an accent outline that thickens inward from a target's edge, for dwell
/// targets whose fill cannot carry the feedback.
/// </summary>
/// <remarks>
/// <para>
/// The keyboard blended each key's fill toward <see cref="Tokens.Accent"/> as dwell
/// accumulated, which destroyed the label's contrast exactly when the user was staring hardest
/// at it: a normal key's label fell from 10.42:1 at rest to <b>1.08:1</b> at full dwell under
/// Midnight, 12.91 to 1.18 under Ember, and 19.17 to 1.25 under Contrast — the theme built for
/// low vision. Every key type collapsed, in every theme.
/// </para>
/// <para>
/// A fill wash cannot be repaired by choosing a different target, which is why the feedback
/// moved off the fill entirely. Keys carry light labels on dark fills, so washing toward light
/// destroys the label, and washing toward dark is imperceptible — measured, blending fully to
/// <see cref="Tokens.StrokeHalo"/> changes a key's own appearance by only 1.41:1 under Midnight
/// and 1.10:1 under Contrast, and at full strength it also renders normal, modifier and active
/// keys identical. Toward the accent was the only direction with a visible delta, and it cost
/// the label.
/// </para>
/// <para>
/// An outline leaves fill and label untouched, so contrast at the end of a dwell equals
/// contrast at rest, while the accent reads 11.22:1 against a normal key's fill (10.98 Ember,
/// 15.29 Contrast). A halo edge grows with it because the accent alone is not enough on every
/// fill: on an ACTIVE modifier under Contrast, accent-on-fill is 1.91:1, where the near-black
/// halo reaches 15.30:1.
/// </para>
/// <para>
/// The emoji grid's fills joined later and hold the same relationship. Cells sit on
/// <c>SurfaceMedium</c>, where the accent reads 11.12:1 (10.22 Ember, 14.69 Contrast); the
/// special buttons on <c>SurfaceKeySwitch</c> (6.27, 7.26, 7.07); the ACTIVE skin-tone swatch
/// on <c>ToggleOn</c>, which is the second case the halo exists for — accent 3.13:1 (3.00 Ember,
/// 1.09 Contrast), halo 5.04 / 4.83 / 15.30. Measured, and gated in
/// <c>DwellOutlineOverlayTests</c> so a fill change re-opens the question.
/// </para>
/// <para>
/// Constant geometry: both outlines exist from construction and only their
/// <see cref="Shape.StrokeThickness"/> moves, so <see cref="SetProgress"/> writes two doubles
/// and allocates nothing. Dwell updates arrive at gaze rate.
/// </para>
/// <para>
/// The fired step (Wave D F18) is a second, additive thickness rule on the same two rectangles:
/// <see cref="SetFired"/> raises them to at least <see cref="FiredThickness"/> for as long as the
/// caller holds it, and the fill's own progress is kept underneath, so a dwell starting while the
/// step is lit neither erases the step nor loses its fill. No colour, fill or visual-tree change.
/// </para>
/// </remarks>
public sealed class DwellOutlineOverlay
{
    /// <summary>
    /// Thickness in DIPs of the accent outline at full dwell.
    ///
    /// Four, not the six the settings controls' progress bar uses: an outline thickens from all
    /// four edges at once, so it consumes twice its thickness across the target's width, and
    /// keyboard keys go down to 35 DIP (<c>BaseKeySize</c> 70 x <c>KeySizeMultiplier</c> 0.5).
    /// A literal layout DIP — there is no spacing token scale to draw from.
    /// </summary>
    public const double MaxThickness = 4;

    /// <summary>
    /// Thickness in DIPs of the fired step's accent outline: half again the full-dwell weight,
    /// so the step reads as BOLDER than the outline that just completed, not as its echo. The
    /// same two bands and the same brushes, only wider (the C1 polarity rule: never a recolour,
    /// never a fill). A literal DIP for the same reason as <see cref="MaxThickness"/>; at 6 DIP
    /// the outline consumes 12 of the smallest key's 35 DIP width, so the label stays clear,
    /// where the ring's 3x core factor would close a 35 DIP key into a frame.
    /// </summary>
    public const double FiredThickness = MaxThickness * 1.5;

    /// <summary>Extra DIPs the halo extends beyond the accent, as a dark edge against bright fills.</summary>
    private const double HaloExtra = 1;

    private readonly Rectangle _halo;
    private readonly Rectangle _accent;

    /// <summary>The last <see cref="SetProgress"/> value, clamped, kept so <see cref="SetFired"/> can re-apply it.</summary>
    private double _progress;

    /// <summary>True while the fired step is lit, independently of the dwell fill.</summary>
    private bool _fired;

    private DwellOutlineOverlay(Grid layout, Rectangle halo, Rectangle accent)
    {
        Layout = layout;
        _halo = halo;
        _accent = accent;
    }

    /// <summary>The container this overlay installed as the target's child.</summary>
    public Grid Layout { get; }

    /// <summary>The accent outline. Exposed so tests can read its real brush and thickness.</summary>
    public Rectangle Outline => _accent;

    /// <summary>The dark edge drawn just outside the accent, for bright fills.</summary>
    public Rectangle Halo => _halo;

    /// <summary>
    /// Wraps <paramref name="content"/> in a layout container carrying the outline, and installs
    /// it as <paramref name="container"/>'s child.
    /// </summary>
    /// <param name="container">The dwell target's root border. Its <c>Child</c> is replaced.</param>
    /// <param name="content">The element the target already displayed. May be <see langword="null"/>.</param>
    /// <returns>The overlay, whose <see cref="SetProgress"/> drives the outline.</returns>
    public static DwellOutlineOverlay Attach(Border container, UIElement? content)
    {
        ArgumentNullException.ThrowIfNull(container);

        // Stroke is drawn centred on the geometry edge, so half of it would fall outside the
        // target. RadiusX/Y follow the container's corner so the outline traces the silhouette
        // rather than cutting across it. All four corners, not just the bottom two: unlike a
        // bottom-edge bar, an outline runs through every corner.
        var radius = container.CornerRadius;
        var corner = Math.Max(
            Math.Max(radius.TopLeft, radius.TopRight),
            Math.Max(radius.BottomLeft, radius.BottomRight));

        var halo = NewOutline(Tokens.BrushStrokeHalo, corner);
        var accent = NewOutline(Tokens.BrushAccent, corner);

        // A Border arranges its Child inside its Padding, so an overlay installed as that Child
        // is inset by it — and an outline inset from the edge stops being an outline. Measured on
        // the prediction buttons (Padding 16,0,16,0): the accent band started 16 DIP in with the
        // halo floating inside the button, leaving a ~13% dead belt at each end and rendering
        // differently from the unpadded keys and category tabs.
        //
        // So the padding moves off the container and onto a wrapper around the content only: the
        // content sits exactly where it did, while the outline spans the target itself. Wrapping
        // rather than setting Margin on the content keeps this working for content that is not a
        // FrameworkElement, and leaves the caller's own element untouched.
        var padding = container.Padding;
        container.Padding = new Thickness(0);

        var layout = new Grid();
        if (content is not null)
        {
            layout.Children.Add(padding == default
                ? content
                : new Border { Padding = padding, Child = content });
        }

        // Halo first, accent over it: the dark edge reads as the outline's outer boundary.
        layout.Children.Add(halo);
        layout.Children.Add(accent);

        // Clip so the outward half of each stroke cannot paint past the target's rounded
        // silhouette — a WPF Border does not clip its Child to CornerRadius. SizeChanged fires
        // only on a real resize, never on a progress write.
        if (corner > 0)
        {
            layout.SizeChanged += (_, e) => layout.Clip = new RectangleGeometry(
                new Rect(e.NewSize), corner, corner);
        }

        container.Child = layout;
        return new DwellOutlineOverlay(layout, halo, accent);
    }

    private static Rectangle NewOutline(Brush stroke, double corner) => new()
    {
        Fill = Brushes.Transparent,
        Stroke = stroke,
        StrokeThickness = 0,
        RadiusX = corner,
        RadiusY = corner,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        IsHitTestVisible = false
    };

    /// <summary>
    /// Thickens the outline to <paramref name="progress"/> of <see cref="MaxThickness"/>,
    /// clamped to [0, 1]. Allocation-free. A live fired step outranks the fill (the outline stays
    /// at least <see cref="FiredThickness"/>); the fill is still recorded, so
    /// <see cref="SetFired"/> false lands on it rather than on zero.
    /// </summary>
    /// <param name="progress">Dwell progress; values outside [0, 1] are clamped.</param>
    public void SetProgress(double progress)
    {
        _progress = Math.Clamp(progress, 0, 1);
        Apply();
    }

    /// <summary>
    /// Whether the fired step is lit (test seam and the caller's own guard).
    /// </summary>
    public bool IsFired => _fired;

    /// <summary>
    /// Turns the fired step on or off (Wave D F18): on, both outlines are at least
    /// <see cref="FiredThickness"/> wide — the same brushes, only wider; off, they return to the
    /// fill's own current progress (zero at rest). Never touches the fill, the content or any
    /// brush, so a fired outline is never less visible than the rest look and the rest look is
    /// exactly restored.
    /// </summary>
    public void SetFired(bool fired)
    {
        if (_fired == fired) return;
        _fired = fired;
        Apply();
    }

    private void Apply()
    {
        var t = _progress * MaxThickness;
        if (_fired) t = Math.Max(t, FiredThickness);

        _accent.StrokeThickness = t;
        // Zero stays zero: a halo on a target showing no progress would read as a permanent
        // border rather than as feedback.
        _halo.StrokeThickness = t > 0 ? t + HaloExtra : 0;
    }
}
