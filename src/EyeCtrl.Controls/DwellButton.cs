namespace EyeCtrl.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Processing;

/// <summary>
/// An atomic dwell-enabled button that invokes an <see cref="Action"/> when the
/// user's gaze dwells on it for the configured duration.
/// </summary>
/// <remarks>
/// <see cref="DwellButton"/> is the building block used both standalone (attached
/// directly to a panel) and inside composite controls such as
/// <see cref="DwellSlider"/>, <see cref="DwellComboBox"/>, and
/// <see cref="DwellColorPicker"/>. Composites may override <see cref="DwellControl.Root"/>
/// properties (width, padding, margin) after construction; child content goes through
/// <see cref="Content"/> — <see cref="DwellControl.Root"/>'s <c>Child</c> is
/// <see cref="DwellButton"/>'s own layout container.
/// <para>
/// A mouse click fallback is wired to <see cref="DwellControl.Root"/> so the button
/// remains usable without eye-tracking.
/// </para>
/// <para>
/// Dwell visual feedback: <see cref="ApplyProgress"/> sweeps <see cref="ProgressBar"/> along
/// the button's bottom edge. The background never moves, so the label's contrast at the end
/// of a dwell equals its contrast at rest.
/// </para>
/// </remarks>
public class DwellButton : DwellControl
{
    /// <summary>
    /// Height of the dwell progress bar in DIPs.
    ///
    /// Six rather than <c>ToolbarWindow</c>'s four: this bar sits inside a control whose whole
    /// visual is the 100 DIP hit circle, and the toolbar's four reads thin at that scale.
    /// Both are literal layout DIPs — there is no spacing token scale to draw from.
    ///
    /// <para>
    /// The bar RESERVES a layout row rather than overlaying the content, so it costs the
    /// content this many DIPs of height: on a default 100 DIP button the label's centre sits at
    /// 47 rather than 50. That is deliberate and measured — the control's own
    /// <c>DesiredSize</c> is unchanged (the outer box does not move; only the content row
    /// shrinks), and the worst real label is two wrapped lines of about 64 DIP against 94 of
    /// room, so nothing clips. Reserving keeps the bar and the label provably unable to
    /// overlap at this scale.
    /// </para>
    /// <para>
    /// A small target should make the opposite choice. Keyboard keys go down to 35 DIP and
    /// carry a single centred glyph, where surrendering 6 DIP would shift every label for no
    /// benefit; those overlay the bar instead. If the two ever converge on one implementation,
    /// this is the axis they differ on — reserve for large targets, overlay for small.
    /// </para>
    /// </summary>
    public const double ProgressBarHeight = 6;

    private readonly Action _action;
    // Assigned once, here, and never touched again by ApplyProgress (which writes
    // _progressScale.ScaleX, not this). Kept as a field so DwellButton owns the brush instance
    // rather than setting Root.Background inline and forgetting it.
    //
    // Note what this does and does not buy: Root is public and its Background is settable, so
    // a caller CAN still replace this brush. What changed is that doing so no longer orphans
    // the dwell feedback — that lives on _progressBar, a separate element — which is precisely
    // the DwellColorPicker bug this plan fixed. Nothing asserts brush identity; the guarantee
    // that matters is pinned by DwellColorPickerProgressTests, which replaces Root.Background
    // and then asserts the bar still sweeps.
    private readonly SolidColorBrush _brush;
    // The button's own layout container. Root.Child belongs to DwellButton from here on:
    // Task 3 puts the progress bar in row 1, and a caller assigning Root.Child would throw
    // it away along with the label. Callers use Content instead.
    private readonly Grid _layout;
    private UIElement? _content;

    // Mutated in place by ApplyProgress at gaze rate: no new brush/transform/geometry is
    // allocated per tick (the ScaleX write itself boxes like any WPF DP set — same cost as
    // the _brush.Color write it replaced).
    private readonly Rectangle _progressBar;
    private readonly ScaleTransform _progressScale;

    /// <summary>
    /// The element drawn inside the button, above the dwell progress bar.
    /// <para>
    /// Replaces direct assignment to <see cref="DwellControl.Root"/>'s <c>Child</c>, which the
    /// composites used to do: <see cref="Root"/>'s child is now this control's own two-row
    /// layout, and overwriting it would silently remove the progress bar. Setting <c>null</c>
    /// clears the content and keeps the bar, but no caller does so today: the colour swatch was
    /// the one <c>null</c> case, and it now sets a panel holding a colour chip above its
    /// caption — a name carried only by a tooltip is unreachable without hover.
    /// </para>
    /// </summary>
    public UIElement? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value))
                return;

            if (_content is not null)
                _layout.Children.Remove(_content);

            if (value is not null)
            {
                Grid.SetRow(value, 0);
                // Index 0 keeps the content behind the bar in z-order. The two occupy
                // different rows, so they do not overlap — this only fixes paint order if a
                // future caller sets a content element that spans both rows.
                _layout.Children.Insert(0, value);
            }

            _content = value;
        }
    }

    /// <summary>
    /// The dwell progress bar drawn along the button's bottom edge.
    ///
    /// A separate element rather than a wash over <see cref="DwellControl.Root"/>'s background,
    /// for two reasons. The label's contrast no longer decays as the dwell completes (the wash
    /// took a light label on a dark fill to 2.62:1 at full dwell). And a caller that replaces
    /// <c>Root.Background</c> — <see cref="DwellColorPicker"/> does, for every swatch — can no
    /// longer orphan the feedback, which is why swatches used to show no progress at all.
    /// </summary>
    public Rectangle ProgressBar => _progressBar;

    /// <summary>
    /// Creates a <see cref="DwellButton"/>.
    /// </summary>
    /// <param name="registrar">The dwell registrar for this overlay.</param>
    /// <param name="id">Unique target identifier within <paramref name="category"/>.</param>
    /// <param name="label">Text displayed inside the button. May be empty for icon-only buttons.</param>
    /// <param name="action">Invoked when dwell completes or the button is clicked.</param>
    /// <param name="baseColor">Resting background colour. The dwell bar is drawn over it, not blended into it.</param>
    /// <param name="category">Dwell category this button is registered under.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels.</param>
    /// <param name="dwellMs">Dwell duration in milliseconds.</param>
    /// <param name="width">Logical width of the button in WPF units. Pass 0 for auto-width.</param>
    /// <param name="height">Logical height of the button in WPF units.</param>
    /// <param name="textBrush">Label foreground. Defaults to white (suitable for dark surface fills);
    /// pass a dark brush for light semantic fills (AccentWarning/AccentDanger) so the label clears WCAG.</param>
    public DwellButton(
        IDwellRegistrar registrar,
        string id,
        string label,
        Action action,
        Color baseColor,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs,
        double width = 100,
        // WYSIWYG dwell floor: a settings dwell control is never drawn smaller than the hit
        // circle it registers (100 DIP diameter at ControlBtnRadius 50), so what you dwell is
        // what you see — for every user, regardless of size preference. Callers may draw
        // bigger; never smaller.
        double height = 100,
        Brush? textBrush = null)
        : base(registrar, id, category, radiusPhysical, dwellMs)
    {
        _action = action;

        Root.Width = width;
        Root.Height = height;
        Root.Margin = new Thickness(4, 0, 4, 0);
        // Also the swatch radius: DwellColorPicker deliberately no longer overrides this.
        Root.CornerRadius = Tokens.StandardCornerRadius;
        _brush = new SolidColorBrush(baseColor);
        Root.Background = _brush;
        Root.Cursor = Cursors.Hand;

        _layout = new Grid();
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _progressScale = new ScaleTransform(0, 1);
        _progressBar = new Rectangle
        {
            Height = ProgressBarHeight,
            Fill = Tokens.BrushAccent,
            // Halo outline, matching the cursor ring's fix for the same problem: a flat
            // accent fill on a button whose own fill is near the accent is invisible.
            // Measured — SettingsWindow's AccentWarning Reset/Delete buttons land around
            // 1.73:1, and they are what carry this justification today. (The starkest case is
            // now historical: DwellColorPicker's first swatch WAS theme.Accent, a literal
            // 1.00:1, until swatches became neutral cards carrying the preset colour on an
            // inner chip, which left the bar on a neutral card instead.)
            // DwellButtonProgressBarContrastTests gates the bar's perceptibility — whichever of
            // its fill or its halo reads better — at >= 3:1 against every fill DwellButton is
            // constructed with, in every theme.
            Stroke = Tokens.BrushStrokeHalo,
            StrokeThickness = 1,
            // Stretch + a ScaleX transform, not a Width: Width would need ActualWidth, which is
            // 0 until the first layout pass — and SettingsAutoPagination measures controls that
            // are never laid out. Scaling is layout-independent and costs one double per tick.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransform = _progressScale,
            // Y is irrelevant here: ScaleY is fixed at 1, so the origin's Y component never
            // participates in any transform this control applies. X = 0 is what matters —
            // it is why the bar grows from the left edge instead of from its centre.
            RenderTransformOrigin = new Point(0, 0),
            IsHitTestVisible = false
        };
        Grid.SetRow(_progressBar, 1);
        _layout.Children.Add(_progressBar);

        Root.Child = _layout;

        // Border does not clip its Child to CornerRadius (a WPF quirk, not a bug in Root's
        // setup above): without this, the progress bar's square corners paint accent pixels
        // outside the rounded silhouette at both bottom corners, on every dwell. Measured via
        // RenderTargetBitmap on a 100x100 button: (50,129), (149,129) and (50,126) were host
        // background at rest and accent at full dwell — the ~5 DIP wedge the corner radius is
        // supposed to cut away. SizeChanged fires only on an actual resize, so this allocates
        // one RectangleGeometry per resize, never per gaze tick.
        _layout.SizeChanged += (_, e) => _layout.Clip = new RectangleGeometry(
            new Rect(e.NewSize), Tokens.CornerRadiusStandard, Tokens.CornerRadiusStandard);

        Content = new TextBlock
        {
            Text = label,
            // Default white text suits the dark surface fills; light semantic fills
            // (AccentWarning/AccentDanger) need dark text to clear WCAG, so the caller
            // passes a textBrush for those.
            Foreground = textBrush ?? Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeTitle,
            FontWeight = Tokens.FontWeightStrong,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // Readability: a label wider than the button's text room WRAPS to a second
            // centered line instead of clipping mid-word. Every label that fits stays on
            // one line — wrap only engages on overflow (found 2026-08-17: the Models
            // download rows needed up to 561 DIP against 360 of room and clipped a third
            // of their text).
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };

        Root.MouseLeftButtonDown += (_, _) => _action();
    }

    /// <inheritdoc/>
    protected override Border CreateRoot() => new();

    /// <inheritdoc/>
    protected override void OnActivated() => _action();

    /// <inheritdoc/>
    protected override void ApplyProgress(double progress)
    {
        _progressScale.ScaleX = Math.Clamp(progress, 0, 1);
    }
}
