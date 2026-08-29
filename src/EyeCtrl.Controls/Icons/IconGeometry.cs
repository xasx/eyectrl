namespace EyeCtrl.Controls.Icons;

using System;
using System.Windows.Media;

/// <summary>
/// One icon's two layers. Both are optional but at least one is always present: most icons are
/// stroke-only, a few are fill-only, and the click icons are both.
///
/// <para>
/// Two layers rather than one because WPF applies a single <c>StrokeThickness</c> and a single
/// <c>Fill</c> per <c>Path</c>. The mouse body is an outline and the pressed button is a solid,
/// so they cannot share one Path without the fill swallowing the outline.
/// </para>
/// </summary>
/// <param name="Stroke">Outlined paths, drawn at <see cref="ActionIcons.GridStrokeThickness"/>.</param>
/// <param name="Fill">Solid paths, drawn with no stroke.</param>
public sealed record IconGeometry(Geometry? Stroke, Geometry? Fill)
{
    /// <summary>
    /// Parses <paramref name="stroke"/> and <paramref name="fill"/> path data, freezing each
    /// non-null result before returning it. Both icon registries now share this one
    /// parse-and-freeze path instead of each repeating it, but that alone does not make the
    /// freeze invariant hold by construction: <see cref="IconGeometry"/>'s own positional
    /// constructor still accepts unfrozen <see cref="Geometry"/> directly, so the per-registry
    /// freeze tests remain the guard for a caller that bypasses this factory.
    ///
    /// <para>
    /// The one invariant this factory does enforce at construction: the result always has at
    /// least one layer. A caller that passes null for both gets an <see cref="ArgumentException"/>
    /// rather than a silently empty icon.
    /// </para>
    /// </summary>
    /// <param name="stroke">Outline path data, or null for a fill-only icon.</param>
    /// <param name="fill">Solid path data, or null (the default) for a stroke-only icon.</param>
    /// <returns>The parsed, frozen geometry pair.</returns>
    /// <exception cref="ArgumentException">Both <paramref name="stroke"/> and <paramref name="fill"/> are null.</exception>
    public static IconGeometry FromPathData(string? stroke, string? fill = null)
    {
        if (stroke is null && fill is null)
            throw new ArgumentException("An icon needs at least one of stroke or fill.", nameof(stroke));

        return new(Parse(stroke), Parse(fill));
    }

    private static Geometry? Parse(string? data)
    {
        if (data is null) return null;
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
