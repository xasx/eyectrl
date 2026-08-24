namespace EyeCtrl.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Processing;

/// <summary>
/// A composite dwell control that presents a numeric value with decrement (−) and
/// increment (+) buttons on either side of a value plate.
/// </summary>
/// <remarks>
/// The value is clamped to [<c>min</c>, <c>max</c>] and rounded to <c>decimals</c>
/// decimal places on each adjustment. Both buttons support dwell activation and
/// mouse-click fallback.
/// <para>
/// The plate between the buttons carries the range as well as the value: the current value at
/// display size, over a rail filled to that value's position in [<c>min</c>, <c>max</c>] and
/// labelled with both ends. That is what tells a user dwelling (+) whether they are near an
/// end and whether the next activation will move anything — a number on its own names neither
/// the range nor the step, and the rail says both without a word of help text.
/// </para>
/// </remarks>
public class DwellSlider : IDwellComposite
{
    private static Color BtnBase => Tokens.SurfaceMedium;

    /// <summary>
    /// The steppers' outline, in DIPs.
    ///
    /// <para>
    /// The (−) and (+) buttons are each drawn 100 x 100 — the WYSIWYG floor, exactly the hit
    /// circle they register — and yet live testing on the tracker reported them as "too small /
    /// hard to aim at". They are not small; they have no EDGE. <see cref="BtnBase"/> is
    /// <see cref="Tokens.SurfaceMedium"/> and the settings page behind them is
    /// <see cref="Tokens.SurfaceDark"/>, which is a 1.23 / 1.23 / 1.14 fill step across
    /// Midnight / Ember / Contrast — sampled off a real render, <c>#292944</c> on <c>#19192D</c>.
    /// So the 100 DIP extent is invisible and the user ends up aiming at the bare glyph.
    /// Palette rule 1 (docs/design/overlay-unification-spec.md:284-286, gated by
    /// tools/palette_validate.py:352-365) says where the fix goes: the border carries the
    /// boundary, the fill only suggests depth.
    /// </para>
    /// <para>
    /// <b>Why <see cref="Tokens.BrushTextSecondary"/> and not <c>BorderSubtle</c>.</b> Measured
    /// against <see cref="Tokens.SurfaceDark"/>: <c>BorderSubtle</c> is 3.35 / 3.32 / 21.00 and
    /// <c>TextSecondary</c> is 6.92 / 8.34 / 17.14. Both clear the 3:1 WCAG 1.4.11 floor for a
    /// non-text component boundary, but <c>BorderSubtle</c> clears it by 0.32 in Ember — a
    /// rounding error away from failing — while <c>TextSecondary</c> has real margin in every
    /// theme. It is also the precedent: <c>DwellToggle</c>'s switch outline is the same brush for
    /// the same reason, and a stepper and a switch play the same role, the thing the user aims at.
    /// </para>
    /// <para>
    /// The content card's own outline is deliberately <c>BorderSubtle</c> and must stay so: a
    /// passive grouping and an active dwell target are different jobs, and the quieter brush is
    /// right for the one that is only saying "the list stops here". Do not "unify" the two.
    /// </para>
    /// <para>
    /// A literal rather than <see cref="Tokens.BorderThicknessStandard"/>, for
    /// <c>DwellToggle.SwitchOutline</c>'s reason: this is a dwell target's perceptibility, so it
    /// must not move with a theme's card-outline weight (1 DIP in Midnight and Ember). Two matches
    /// the switch on the same page, so the aimable controls of a settings page carry one edge
    /// weight.
    /// </para>
    /// <para>
    /// It costs neither layout nor dwell feedback. <see cref="DwellButton"/> gives
    /// <see cref="DwellControl.Root"/> an explicit <c>Width</c> and <c>Height</c>
    /// (DwellButton.cs:165-166), so the border deflates the CHILD and the outer box stays exactly
    /// 100 x 100 — measured before and after: Actual 100.00 x 100.00 either way, Desired
    /// 108.00 x 100.00, and the composite's natural row width unchanged at 404.00. And the dwell
    /// bar lives on <c>DwellButton.ProgressBar</c> inside that child layout, not on Root's border,
    /// so the two cannot collide — the same arrangement <see cref="DwellColorPicker"/>'s swatch
    /// border and <c>DwellToggle</c>'s switch outline already rely on.
    /// </para>
    /// <para>
    /// Scoped to this composite rather than written into <see cref="DwellButton"/>: that type is
    /// shared with the toolbar, the keyboard, the modals and the quick-action tiles, which sit on
    /// other backdrops and carry their own contrast gates.
    /// </para>
    /// </summary>
    private const double StepperOutline = 2;

    /// <summary>
    /// Plate width in DIPs. Sized so the whole row fits the narrowest content column the app
    /// supports: 108 + 188 + 108 = 404 against 416 (an 800 DIP display at the 0.9 window
    /// ratio, less a 240 sidebar and two 16 DIP margin pairs). Guarded by
    /// SettingsContentFitTests.TheSliderRow_FitsTheContentColumn_OnANarrowDisplay.
    /// </summary>
    private const double PlateWidth = 180;

    /// <summary>
    /// Plate height in DIPs. Must equal the stepper buttons' height either side, which is
    /// <see cref="DwellButton"/>'s default — this type does not pass one.
    /// <c>SettingsAutoPagination</c> measures row heights to decide where a settings page
    /// breaks, so a plate taller than the buttons would silently re-paginate every page that
    /// carries a slider. DwellSliderPlateTests.ThePlate_IsExactlyAsTallAsItsStepperButtons
    /// compares the two rather than either against a literal, so a change to that default
    /// fails there instead of drifting.
    /// </summary>
    private const double PlateHeight = 100;

    /// <summary>Rail thickness in DIPs, for the track and the fill inside it alike.</summary>
    private const double RailHeight = 5;

    private readonly DwellLabel _label;
    private readonly DwellButton _minus;
    private readonly DwellButton _plus;
    private readonly TextBlock _valueText;
    private readonly TextBlock _minText;
    private readonly TextBlock _maxText;
    private readonly Border _plate;
    private readonly Border _railTrack;
    private readonly Rectangle _railFill;
    private readonly ScaleTransform _railScale;
    private readonly StackPanel _row;

    private readonly Func<double> _get;
    private readonly Action<double> _set;
    private readonly double _min;
    private readonly double _max;
    private readonly double _step;
    private readonly int _decimals;
    private readonly bool _showLabel;

    /// <summary>
    /// Cached once in the constructor, not rebuilt per get: SettingsWindow's
    /// UpdateAllTargetPositions iterates <see cref="Children"/> for every attached composite on
    /// every gaze frame, and the collection expression this replaced (<c>=&gt; [_minus, _plus]</c>)
    /// materialised a fresh array each time — per-frame garbage on the gaze path, where a Gen0
    /// collection suspends the gaze thread. DwellComboBox and DwellColorPicker reach the same
    /// property by returning their backing <c>List&lt;DwellButton&gt;</c> field.
    /// </summary>
    private readonly DwellControl[] _children;

    /// <inheritdoc/>
    public IReadOnlyList<DwellControl> Children => _children;

    /// <summary>The current value, rendered at display size in the plate.</summary>
    internal TextBlock ValueText => _valueText;

    /// <summary>The minimum end-label under the rail.</summary>
    internal TextBlock MinimumText => _minText;

    /// <summary>The maximum end-label under the rail.</summary>
    internal TextBlock MaximumText => _maxText;

    /// <summary>The surface the value and both end-labels are read against.</summary>
    internal Border Plate => _plate;

    /// <summary>
    /// The rail's empty groove — the surface <see cref="RailFill"/> has to be visible against.
    /// </summary>
    internal Border RailTrack => _railTrack;

    /// <summary>
    /// The filled part of the rail: stretched to the track's full width, then scaled down to
    /// <see cref="RailFraction"/> about its left edge.
    /// </summary>
    internal Rectangle RailFill => _railFill;

    /// <summary>How far along its range the value sits, in [0, 1]. Zero on a degenerate range.</summary>
    internal double RailFraction => _railScale.ScaleX;

    /// <summary>
    /// Creates a <see cref="DwellSlider"/>.
    /// </summary>
    /// <param name="registrar">The dwell registrar for this overlay.</param>
    /// <param name="idPrefix">
    /// Prefix used to generate unique target identifiers for the minus and plus buttons
    /// (<c>{idPrefix}_dn</c> and <c>{idPrefix}_up</c>).
    /// </param>
    /// <param name="label">Descriptive heading displayed above the slider row.</param>
    /// <param name="min">Minimum allowed value (inclusive).</param>
    /// <param name="max">Maximum allowed value (inclusive).</param>
    /// <param name="step">Amount added or subtracted per button activation.</param>
    /// <param name="decimals">
    /// Number of decimal places the value is rounded to.
    /// Pass 0 for integer values.
    /// </param>
    /// <param name="get">Returns the current value of the setting.</param>
    /// <param name="set">Applies the new value when a button activates.</param>
    /// <param name="category">Dwell category the buttons are registered under.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels.</param>
    /// <param name="dwellMs">Dwell duration in milliseconds.</param>
    /// <param name="showLabel">
    /// Whether this composite draws its own heading above its controls. False when the host
    /// lays the name out itself — the settings window's two-column rows put the name in their
    /// left column, and a second copy inside the composite would duplicate it.
    /// </param>
    public DwellSlider(
        IDwellRegistrar registrar,
        string idPrefix,
        string label,
        double min,
        double max,
        double step,
        int decimals,
        Func<double> get,
        Action<double> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs,
        bool showLabel = true)
    {
        _get = get;
        _set = set;
        _min = min;
        _max = max;
        _step = step;
        _decimals = decimals;
        _showLabel = showLabel;

        _label = new DwellLabel(label);

        // Read once and feed both the number and the rail. Two calls would let a non-pure getter
        // put the plate in a state it can never reach again: a number saying one thing and a rail
        // saying another, from the very first frame.
        var initial = get();

        _valueText = new TextBlock
        {
            Text = FormatVal(initial),
            Foreground = Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeDisplay,
            FontWeight = Tokens.FontWeightStrong,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // TextSecondary, not TextDim: FontSizeSubtitle is 18 DIP at normal weight, which is NOT
        // WCAG "large text" (that needs >= 24 DIP, or >= 18.66 DIP bold), so these have to clear
        // 4.5:1 rather than 3:1. Against SurfaceMedium, TextDim measures 4.37:1 in Midnight and
        // fails. Theme.SliderPlateContrastTests gates this in every theme.
        _minText = new TextBlock
        {
            Text = Format(decimals, min),
            Foreground = Tokens.BrushTextSecondary,
            FontSize = Tokens.FontSizeSubtitle,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        _maxText = new TextBlock
        {
            Text = Format(decimals, max),
            Foreground = Tokens.BrushTextSecondary,
            FontSize = Tokens.FontSizeSubtitle,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        // Same stretch-and-scale trick as DwellButton's progress bar: a Width would need
        // ActualWidth, which is 0 until layout, and SettingsAutoPagination measures rows that
        // are never laid out.
        _railScale = new ScaleTransform(0, 1);
        _railFill = new Rectangle
        {
            Height = RailHeight,
            Fill = Tokens.BrushAccent,
            RadiusX = RailHeight / 2,
            RadiusY = RailHeight / 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransform = _railScale,
            RenderTransformOrigin = new Point(0, 0.5)
        };

        // The track and the fill above it round from different sources on purpose. A Border's
        // CornerRadius must be token-backed (RawGeometryLiteralTests scans that property; it does
        // not scan a Shape's RadiusX/RadiusY), and the token cannot be CircleCornerRadius, which
        // CornerRadiusClampTests reserves for square elements — on a long thin one it inscribes
        // an ellipse rather than a stadium. So the track takes SmallCornerRadius, which WPF
        // clamps per axis to at most half the track's height whatever the theme sets it to, while
        // the fill takes the exact semicircular RailHeight / 2 no token expresses.
        _railTrack = new Border
        {
            Height = RailHeight,
            CornerRadius = Tokens.SmallCornerRadius,
            Background = Tokens.BrushSurfaceDark,
            Child = _railFill
        };

        var railRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        railRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        railRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        railRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_minText, 0);
        Grid.SetColumn(_railTrack, 1);
        Grid.SetColumn(_maxText, 2);
        railRow.Children.Add(_minText);
        railRow.Children.Add(_railTrack);
        railRow.Children.Add(_maxText);

        var plateContent = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        plateContent.Children.Add(_valueText);
        plateContent.Children.Add(railRow);

        _plate = new Border
        {
            Width = PlateWidth,
            Height = PlateHeight,
            Margin = new Thickness(4, 0, 4, 0),
            CornerRadius = Tokens.StandardCornerRadius,
            Background = Tokens.BrushSurfaceMedium,
            Padding = new Thickness(14, 0, 14, 0),
            Child = plateContent
        };

        _minus = new DwellButton(registrar, idPrefix + "_dn", "\u2212",
            () => Adjust(-_step), BtnBase, category, radiusPhysical, dwellMs);

        _plus = new DwellButton(registrar, idPrefix + "_up", "+",
            () => Adjust(_step), BtnBase, category, radiusPhysical, dwellMs);

        _children = [_minus, _plus];

        // Give both steppers a visible edge — see StepperOutline for the measurements and for why
        // the brush is TextSecondary rather than the card's BorderSubtle. Written here, on this
        // composite's own children, exactly as DwellColorPicker.cs:190-191 and DwellToggle.cs:367-368
        // do; DwellButton itself is left alone.
        foreach (var stepper in _children)
        {
            stepper.Root.BorderThickness = new Thickness(StepperOutline);
            stepper.Root.BorderBrush = Tokens.BrushTextSecondary;
        }

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            // The 24 DIP bottom margin is the row rhythm a SELF-LABELLED composite carries:
            // heading, controls, gap, next row. The headless form has no heading and is dropped
            // into a host row that owns the rhythm itself, so it carries none — exactly as
            // DwellToggle's headless form does. Left unconditional, the extra 24 pushed the
            // control's visual centre 12 DIP above the name it is supposed to sit beside, and
            // 12 DIP off the toggles on the same page, which never had one.
            Margin = showLabel ? new Thickness(0, 0, 0, 24) : new Thickness(0)
        };

        _row.Children.Add(_minus.Root);
        _row.Children.Add(_plate);
        _row.Children.Add(_plus.Root);

        UpdateRail(initial);
    }

    /// <inheritdoc/>
    public void Attach(Panel parent)
    {
        if (_showLabel)
            _label.Attach(parent);

        parent.Children.Add(_row);
        _minus.RegisterDwell();
        _plus.RegisterDwell();
    }

    /// <inheritdoc/>
    public void Detach(Panel parent)
    {
        if (_showLabel)
            _label.Detach(parent);

        parent.Children.Remove(_row);
        _minus.UnregisterDwell();
        _plus.UnregisterDwell();
    }

    private void Adjust(double delta)
    {
        var v = Math.Round(Math.Clamp(_get() + delta, _min, _max), _decimals);
        _set(v);
        _valueText.Text = FormatVal(v);
        UpdateRail(v);
    }

    /// <summary>
    /// Sets the rail to <paramref name="value"/>'s position in [min, max].
    ///
    /// Guards min == max rather than letting it produce NaN: a NaN ScaleX silently paints
    /// nothing, which looks exactly like a value pinned at the minimum.
    /// </summary>
    /// <param name="value">The value the rail should report.</param>
    private void UpdateRail(double value)
    {
        var span = _max - _min;
        _railScale.ScaleX = span <= 0
            ? 0
            : Math.Clamp((value - _min) / span, 0, 1);
    }

    private string FormatVal(double v) => Format(_decimals, v);

    internal static string Format(int decimals, double v) =>
        decimals == 0 ? ((int)v).ToString(System.Globalization.CultureInfo.InvariantCulture)
                      : v.ToString($"F{decimals}", System.Globalization.CultureInfo.InvariantCulture);
}
