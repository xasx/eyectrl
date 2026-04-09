namespace EyeCtrl.App.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

public class DwellSlider : IDwellComposite
{
    private static readonly Color BtnBase = Color.FromArgb(200, 60, 60, 80);

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

    public IEnumerable<DwellControl> Children => [_minus, _plus];

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
            Foreground = Brushes.White,
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

    public void Attach(Panel parent)
    {
        _label.Attach(parent);
        parent.Children.Add(_row);
        _minus.RegisterDwell();
        _plus.RegisterDwell();
    }

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
