namespace EyeCtrl.Controls.Icons;

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
    /// non-null result before returning it. Every icon registry builds its entries through this
    /// factory rather than parsing and freezing by hand, so the freeze invariant holds by
    /// construction instead of by convention plus a per-registry test.
    /// </summary>
    /// <param name="stroke">Outline path data, or null for a fill-only icon.</param>
    /// <param name="fill">Solid path data, or null (the default) for a stroke-only icon.</param>
    /// <returns>The parsed, frozen geometry pair.</returns>
    public static IconGeometry FromPathData(string? stroke, string? fill = null)
        => new(Parse(stroke), Parse(fill));

    private static Geometry? Parse(string? data)
    {
        if (data is null) return null;
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
