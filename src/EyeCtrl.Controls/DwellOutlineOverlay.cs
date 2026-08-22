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
/// Constant geometry: both outlines exist from construction and only their
/// <see cref="Shape.StrokeThickness"/> moves, so <see cref="SetProgress"/> writes two doubles
/// and allocates nothing. Dwell updates arrive at gaze rate.
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

    /// <summary>Extra DIPs the halo extends beyond the accent, as a dark edge against bright fills.</summary>
    private const double HaloExtra = 1;

    private readonly Rectangle _halo;
    private readonly Rectangle _accent;

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
        // rather than cutting across it.
        var radius = container.CornerRadius;
        var corner = Math.Max(radius.BottomLeft, radius.BottomRight);

        var halo = NewOutline(Tokens.BrushStrokeHalo, corner);
        var accent = NewOutline(Tokens.BrushAccent, corner);

        var layout = new Grid();
        if (content is not null)
        {
            layout.Children.Add(content);
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
    /// clamped to [0, 1]. Allocation-free.
    /// </summary>
    /// <param name="progress">Dwell progress; values outside [0, 1] are clamped.</param>
    public void SetProgress(double progress)
    {
        var t = Math.Clamp(progress, 0, 1) * MaxThickness;
        _accent.StrokeThickness = t;
        // Zero stays zero: a halo on a target showing no progress would read as a permanent
        // border rather than as feedback.
        _halo.StrokeThickness = t > 0 ? t + HaloExtra : 0;
    }
}
