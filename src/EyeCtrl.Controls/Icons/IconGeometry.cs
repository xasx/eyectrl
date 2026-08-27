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
public sealed record IconGeometry(Geometry? Stroke, Geometry? Fill);
