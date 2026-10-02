namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Models;

/// <summary>
/// The single painting path for the action-menu ring: the runtime overlay
/// (<see cref="ActionMenuWindow"/>) and, later, the settings preview both draw through
/// <see cref="Paint"/>.
///
/// <para>
/// <b>Exactly one <c>DwellArcButton.Create</c> caller for the ring, and this is it.</b> A second
/// call site could drift inside the argument list — a different brush, a scaled radius — with no
/// test seeing it. The runtime parity gate (<c>ActionMenuRingPainterTests</c>) and the
/// <c>src/</c> call-site scans keep this file the only one that passes the ring radius into
/// <c>Create</c>.
/// </para>
///
/// <para>
/// <b>Zero dwell targets, by type signature.</b> The painter takes no <c>IDwellRegistrar</c> and
/// constructs no <c>DwellControl</c> — registration stays on the window's own path
/// (<c>ActionMenuViewModel.BuildLayer</c>). A preview painted through this method is structurally
/// incapable of registering a target, which is the property the settings preview's safety rests
/// on.
/// </para>
///
/// <para>
/// <b>Never a scaled radius.</b> <see cref="DwellArcButton"/>'s stroke model is not scale
/// invariant — <c>MinArcThickness</c> is a 4 DIP floor and the rim constants are flat — so
/// <c>Create(s × radius)</c> silently produces a different button. Every call here passes
/// <see cref="ActionMenuGeometry.ButtonRadius"/>. The preview's scale is a <c>LayoutTransform</c>
/// over a true-size painting (see <see cref="PaintScaleIsLayoutTransform"/>).
/// </para>
///
/// <para>
/// <b>Two documented modes, one code path.</b> At scale ≥ 1 the painting is today's runtime
/// output exactly: one button per slot, label passed into <c>Create</c> when the caller asks for
/// labels (the Help toggle). Below scale 1 — the settings miniature — buttons are still created
/// at the true radius and placed at their true centres inside a child canvas carrying the
/// <c>LayoutTransform</c>, but WITHOUT the label argument; the labels are separate unscaled
/// TextBlocks placed at the scaled RUNTIME label position (under each icon, inside the button —
/// never centred on the glyph, which obscured it at preview scales), because an in-subtree label
/// at those scales renders at ~5-6 pt, too small to read. The preview's More slot gains the " +N"
/// annotation and the preview never receives a Back slot: composition is the plan's
/// (<see cref="RingPlan.BuildLayer"/> with <c>showBack: false</c>), and this painter draws exactly
/// the slots it is given, never adding or dropping one.
/// </para>
/// </summary>
internal static class ActionMenuRingPainter
{
    /// <summary>
    /// Gap between the icon and its label as a fraction of the button diameter, restated from
    /// <c>DwellArcButton</c>'s private <c>LabelGapRatio</c>. The preview label is positioned at
    /// the RUNTIME label placement (below the icon, inside the button) scaled by the preview's
    /// scalar, so this arithmetic has to mirror the button factory; the scaled-mode parity test
    /// pins it against a labelled true-size button's own label.
    /// </summary>
    private const double RuntimeLabelGapRatio = 0.05;

    /// <summary>
    /// The runtime label's line box as a multiple of its font size, restated from
    /// <c>DwellArcButton</c>'s private <c>LabelLineHeightRatio</c>. Same reason as
    /// <see cref="RuntimeLabelGapRatio"/>.
    /// </summary>
    private const double RuntimeLabelLineHeightRatio = 1.1;

    /// <summary>
    /// Group opacity for disabled (<see cref="RingPaintSlot.IsDisabled"/>) ring slots.
    /// The value the dim contrast test validates (<c>ActionIconDimContrastTests</c>) and the dim
    /// render test reads (<c>ActionMenuWindowDimRenderTests</c>): 0.45 measured 2.30:1
    /// dimmed-icon vs dimmed-fill on Midnight/#FFFFFF — below any usable floor. Adjust ONLY with
    /// a measured note.
    /// </summary>
    internal const double DisabledOpacity = 0.6;

    /// <summary>
    /// Paints one built layer's slots onto <paramref name="canvas"/> and returns the created
    /// buttons keyed by slot id, so the runtime window can route dwell progress to them. The
    /// returned map is empty only for an empty slot list.
    /// </summary>
    /// <param name="canvas">The canvas to paint into.</param>
    /// <param name="slots">The layer's slots, already positioned in canvas coordinates.</param>
    /// <param name="options">Runtime or preview painting mode.</param>
    /// <returns>The painted buttons, keyed by slot id.</returns>
    internal static IReadOnlyDictionary<string, DwellArcButton> Paint(
        Canvas canvas,
        IReadOnlyList<RingPaintSlot> slots,
        RingPaintOptions options)
    {
        var buttons = new Dictionary<string, DwellArcButton>(slots.Count);

        if (!options.PaintsScaled)
        {
            foreach (var slot in slots)
            {
                var button = DwellArcButton.Create(
                    slot.Center,
                    ActionMenuGeometry.ButtonRadius,
                    symbol: null,
                    icon: ActionIcons.For(slot.Icon),
                    label: options.ShowLabels ? options.LabelFor(slot) : null);
                AddButton(canvas, button, slot.IsDisabled);
                buttons[slot.Id] = button;
            }

            return buttons;
        }

        // Miniature: the true-size painting sits in its own canvas under the LayoutTransform; the
        // labels sit OUTSIDE that transform, so their type stays unscaled while their positions
        // are the transformed runtime label positions.
        var painting = new Canvas { LayoutTransform = new ScaleTransform(options.Scale, options.Scale) };
        canvas.Children.Add(painting);

        // The runtime label sits UNDER the icon, inside the button: at true size its top is this
        // far below the button's centre (DwellArcButton.Create's icon/label stack). The preview
        // label is drawn outside the transform, so its position is that offset SCALED — centring
        // it on the glyph drew type across the icon at the 494 budget (T3 review H1). The parity
        // test pins this against a labelled true-size button, so the restated arithmetic cannot
        // drift silently.
        var trueDiameter = ActionMenuGeometry.ButtonRadius * 2;
        var iconSize = trueDiameter * ContainerRatio.Medium;
        var stackGap = trueDiameter * RuntimeLabelGapRatio;
        var labelHeight = Tokens.FontSizeSubtitle * RuntimeLabelLineHeightRatio;
        var labelTopOffset = (iconSize + stackGap - labelHeight) / 2;

        foreach (var slot in slots)
        {
            var button = DwellArcButton.Create(
                slot.Center,
                ActionMenuGeometry.ButtonRadius,
                symbol: null,
                icon: ActionIcons.For(slot.Icon));
            button.AddTo(painting);
            buttons[slot.Id] = button;

            var label = new TextBlock
            {
                Text = options.LabelFor(slot),
                Foreground = Tokens.BrushTextPrimary,
                FontSize = Tokens.FontSizeSubtitle,
                FontWeight = Tokens.FontWeightMedium,
                FontFamily = Tokens.FontStandard,
                TextAlignment = TextAlignment.Center,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, slot.Center.X * options.Scale - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, (slot.Center.Y + labelTopOffset) * options.Scale);
            canvas.Children.Add(label);
        }

        return buttons;
    }

    /// <summary>
    /// Adds a button's parts to <paramref name="canvas"/>, dimmed as a group when disabled.
    /// </summary>
    private static void AddButton(Canvas canvas, DwellArcButton button, bool disabled)
    {
        if (!disabled)
        {
            button.AddTo(canvas);
            return;
        }

        // Group opacity via a per-button child canvas: AddTo places its parts relative to ANY
        // canvas, so a host canvas at origin carries them unchanged and Opacity dims the whole
        // group at once. Per-part opacity would be neither group opacity nor hazard-free.
        var host = new Canvas { Opacity = DisabledOpacity };
        button.AddTo(host);
        canvas.Children.Add(host);
    }
}

/// <summary>
/// One slot of a painted ring layer: its dwell id, the canvas point its button is centred on, and
/// the icon/label/kind the plan resolved for it.
/// </summary>
/// <param name="Id">The dwell-target id, used to key the returned button map.</param>
/// <param name="Center">The button's centre in the target canvas's coordinates.</param>
/// <param name="Icon">Which drawn icon the slot shows.</param>
/// <param name="Label">The ring name (see <see cref="RingLabelMap"/>).</param>
/// <param name="Kind">What a dwell on the slot would do; the More slot is annotated in preview mode.</param>
/// <param name="IsDisabled">
/// Drawn dimmed at <see cref="ActionMenuRingPainter.DisabledOpacity"/> — a latched modifier's
/// Drag/ScrollMode, which the ViewModel also leaves without a dwell target. Runtime mode only: the
/// preview never sets it (it has no latch and no targets at all), and the scaled mode ignores it.
/// </param>
internal readonly record struct RingPaintSlot(
    string Id,
    Point Center,
    ActionIcon Icon,
    string Label,
    ActionMenuItemKind Kind,
    bool IsDisabled = false);

/// <summary>
/// How the painter draws a layer. The two factories are the two documented modes; the default is
/// runtime's.
/// </summary>
internal sealed record RingPaintOptions
{
    /// <summary>
    /// Painting scale: 1.0 (the default) is true size and the runtime path. Below 1 the painting
    /// is true-size under a <c>LayoutTransform</c>, with unscaled labels placed at the scaled
    /// centres.
    /// </summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>
    /// Whether labels are drawn. Runtime reads the Help toggle; the preview always passes true.
    /// At scale ≥ 1 the label goes into <c>Create</c> (today's argument exactly); below 1 it
    /// becomes a separate TextBlock.
    /// </summary>
    public bool ShowLabels { get; init; }

    /// <summary>
    /// The number of second-layer actions, when the preview should annotate the More slot as
    /// "More +N". Null (runtime) never annotates: the runtime's More says "More".
    /// </summary>
    public int? MoreAnnotationCount { get; init; }

    /// <summary>True when the painting must be scaled — the preview label mode.</summary>
    public bool PaintsScaled => Scale < 1.0;

    /// <summary>The runtime painting mode: true size, labels per the Help toggle.</summary>
    /// <param name="showLabels">Whether the Help toggle has names switched on.</param>
    /// <returns>Options for the runtime ring.</returns>
    internal static RingPaintOptions Runtime(bool showLabels) => new() { ShowLabels = showLabels };

    /// <summary>
    /// The settings-preview painting mode: labels always on, More annotated with the hidden
    /// action count, and everything below scale 1 drawn under the <c>LayoutTransform</c> with
    /// unscaled labels.
    /// </summary>
    /// <param name="scale">The uniform scale from <see cref="RingPlan.ScaleFor"/>.</param>
    /// <param name="secondaryActionCount">How many actions sit behind More.</param>
    /// <returns>Options for the miniature.</returns>
    internal static RingPaintOptions Preview(double scale, int secondaryActionCount) => new()
    {
        Scale = scale,
        ShowLabels = true,
        MoreAnnotationCount = secondaryActionCount,
    };

    /// <summary>
    /// The text this slot's label shows under these options: the slot's ring name, plus the
    /// "+N" annotation on the More slot in preview mode.
    /// </summary>
    /// <param name="slot">The slot being labelled.</param>
    /// <returns>The label text.</returns>
    internal string LabelFor(RingPaintSlot slot) =>
        MoreAnnotationCount is int count and > 0 && slot.Kind == ActionMenuItemKind.OpenMore
            ? $"{slot.Label} +{count}"
            : slot.Label;
}
