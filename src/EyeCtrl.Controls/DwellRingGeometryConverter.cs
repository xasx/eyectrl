namespace EyeCtrl.Controls;

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

/// <summary>
/// Maps a dwell progress (0..1) to the Quick Actions tile's rim arc, expressed in the tile face's
/// 100 DIP box. B8b fixup-01 R1: the old straight progress bar spanned the label's width and sat
/// 25–47 DIP below the disc's centre, so in most presets it was drawn past the disc — and past the
/// r50 hit circle. The arc traces the hit circle at <see cref="RingRadius"/> ± half its stroke, so
/// the feedback is always inside the drawn face and inside the area the dwell counts in.
///
/// <para>
/// A converter rather than a custom control because the value is a single geometry: a
/// <see cref="System.Windows.Shapes.Path"/> in the ItemTemplate takes this as its <c>Data</c>,
/// which keeps the stroke, brush and the paused-opacity cue in XAML as ordinary properties.
/// </para>
/// </summary>
public sealed class DwellRingGeometryConverter : IValueConverter
{
    /// <summary>The face box the arc's coordinates live in, in DIP — the tile's drawn diameter.</summary>
    public const double FaceSize = 100;

    /// <summary>
    /// The arc's centre-line radius in DIP. 45 with the template's 4 DIP stroke paints r43–r47,
    /// inside the inner rim at both chrome weights (r49 at the default 1 DIP border, r48 under
    /// Contrast's 2 DIP border), so the arc is never drawn outside the face at any theme.
    /// </summary>
    public const double RingRadius = 45;

    /// <summary>The shared instance, for <c>{x:Static}</c> in XAML.</summary>
    public static DwellRingGeometryConverter Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        For(value is double progress ? progress : 0);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>
    /// The arc for <paramref name="progress"/>: nothing at 0, a full ring at 1, otherwise a
    /// clockwise arc from 12 o'clock. Mirrors <see cref="DwellArcButton.UpdateArc"/> so the tile's
    /// feedback reads exactly like every other gaze target's.
    /// </summary>
    /// <param name="progress">The dwell progress, clamped by the geometry's own rules.</param>
    public static Geometry For(double progress)
    {
        if (double.IsNaN(progress) || progress <= 0)
            return Geometry.Empty;

        var centre = new Point(FaceSize / 2, FaceSize / 2);
        if (progress >= 1)
            return new EllipseGeometry(centre, RingRadius, RingRadius);

        var angle = progress * 360;
        var radians = angle * Math.PI / 180;
        var figure = new PathFigure
        {
            StartPoint = new Point(centre.X, centre.Y - RingRadius),
            IsClosed = false,
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(
                centre.X + (RingRadius * Math.Sin(radians)),
                centre.Y - (RingRadius * Math.Cos(radians))),
            Size = new Size(RingRadius, RingRadius),
            IsLargeArc = angle > 180,
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
