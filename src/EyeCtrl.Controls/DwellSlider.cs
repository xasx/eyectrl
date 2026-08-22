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
/// labelled with both ends. A bare number said none of that — nothing on screen named the
/// range or the step, so a user dwelling (+) could not tell whether they were near an end or
/// whether the next activation would move anything at all. The rail answers both without a
/// word of help text.
/// </para>
/// </remarks>
public class DwellSlider : IDwellComposite
{
    private static Color BtnBase => Tokens.SurfaceMedium;

    /// <summary>
    /// Plate width in DIPs. Sized so the whole row fits the narrowest content column the app
    /// supports: 108 + 188 + 108 = 404 against 416 (an 800 DIP display at the 0.9 window
    /// ratio, less a 240 sidebar and two 16 DIP margin pairs). Guarded by
    /// SettingsContentFitTests.TheSliderRow_FitsTheContentColumn_OnANarrowDisplay.
    /// </summary>
    private const double PlateWidth = 180;

    /// <summary>
    /// Plate height in DIPs, matched to the stepper buttons either side.
    /// <c>SettingsAutoPagination</c> measures row heights to decide where a settings page
    /// breaks, so a plate taller than the buttons would silently re-paginate every page that
    /// carries a slider. Pinned by
    /// DwellSliderPlateTests.ThePlate_IsNoTallerThanTheRowItSitsIn.
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
    public TextBlock ValueText => _valueText;

    /// <summary>The minimum end-label under the rail.</summary>
    public TextBlock MinimumText => _minText;

    /// <summary>The maximum end-label under the rail.</summary>
    public TextBlock MaximumText => _maxText;

    /// <summary>The surface the value and both end-labels are read against.</summary>
    public Border Plate => _plate;

    /// <summary>
    /// The rail's empty groove — the surface <see cref="RailFill"/> has to be visible against.
    /// </summary>
    public Border RailTrack => _railTrack;

    /// <summary>
    /// The filled part of the rail: stretched to the track's full width, then scaled down to
    /// <see cref="RailFraction"/> about its left edge.
    /// </summary>
    public Rectangle RailFill => _railFill;

    /// <summary>How far along its range the value sits, in [0, 1]. Zero on a degenerate range.</summary>
    public double RailFraction => _railScale.ScaleX;

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
        int dwellMs)
    {
        _get = get;
        _set = set;
        _min = min;
        _max = max;
        _step = step;
        _decimals = decimals;

        _label = new DwellLabel(label);

        _valueText = new TextBlock
        {
            Text = FormatVal(get()),
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

        // SmallCornerRadius rather than a raw RailHeight / 2 — RawGeometryLiteralTests requires a
        // token here — and NOT CircleCornerRadius, which CornerRadiusClampTests reserves for
        // square elements: on a long thin one that token inscribes an ellipse, not a stadium.
        // WPF clamps each radius to half its own axis, so 4 draws as 4 x 2.5 on a 5 DIP track.
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

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 24)
        };

        _row.Children.Add(_minus.Root);
        _row.Children.Add(_plate);
        _row.Children.Add(_plus.Root);

        UpdateRail(get());
    }

    /// <inheritdoc/>
    public void Attach(Panel parent)
    {
        _label.Attach(parent);
        parent.Children.Add(_row);
        _minus.RegisterDwell();
        _plus.RegisterDwell();
    }

    /// <inheritdoc/>
    public void Detach(Panel parent)
    {
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
