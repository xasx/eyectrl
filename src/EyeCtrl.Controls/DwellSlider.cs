namespace EyeCtrl.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Processing;

/// <summary>
/// A composite dwell control that presents a numeric value with decrement (−) and
/// increment (+) buttons on either side of a value display.
/// </summary>
/// <remarks>
/// The value is clamped to [<c>min</c>, <c>max</c>] and rounded to <c>decimals</c>
/// decimal places on each adjustment. Both buttons support dwell activation and
/// mouse-click fallback.
/// </remarks>
public class DwellSlider : IDwellComposite
{
    private static readonly Color BtnBase = Tokens.SurfaceMedium;

    private readonly DwellLabel _label;
    private readonly DwellButton _minus;
    private readonly DwellButton _plus;
    private readonly TextBlock _valueText;
    private readonly StackPanel _row;

    private readonly Func<double> _get;
    private readonly Action<double> _set;
    private readonly double _min;
    private readonly double _max;
    private readonly double _step;
    private readonly int _decimals;

    /// <inheritdoc/>
    public IEnumerable<DwellControl> Children => [_minus, _plus];

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
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Width = 140,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _minus = new DwellButton(registrar, idPrefix + "_dn", "\u2212",
            () => Adjust(-_step), BtnBase, category, radiusPhysical, dwellMs);

        _plus = new DwellButton(registrar, idPrefix + "_up", "+",
            () => Adjust(_step), BtnBase, category, radiusPhysical, dwellMs);

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 12)
        };

        _row.Children.Add(_minus.Root);
        _row.Children.Add(_valueText);
        _row.Children.Add(_plus.Root);
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
    }

    private string FormatVal(double v) =>
        _decimals == 0 ? ((int)v).ToString() : v.ToString($"F{_decimals}");
}
