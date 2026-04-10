namespace EyeCtrl.App.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

public class DwellComboBox : IDwellComposite
{
    private static readonly Color BtnBase = Color.FromArgb(200, 60, 60, 80);
    private static readonly Color SelectedColor = Color.FromArgb(200, 30, 100, 30);

    private readonly DwellLabel _label;
    private readonly List<DwellButton> _options = new();
    private readonly List<TextBlock> _optionTexts = new();
    private readonly List<Border> _optionBorders = new();
    private readonly WrapPanel _panel;

    private readonly string[] _displayOptions;
    private readonly string[] _codeOptions;
    private readonly Func<string> _getCode;
    private readonly Action<string> _setCode;

    public IEnumerable<DwellControl> Children => _options;

    private void UpdateOptionSelectionStyles()
    {
        var selectedCode = _getCode();

        for (int i = 0; i < _optionBorders.Count && i < _codeOptions.Length; i++)
        {
            var isSelected = _codeOptions[i] == selectedCode;
            _optionBorders[i].BorderBrush = new SolidColorBrush(isSelected ? SelectedColor : BtnBase);
            _optionBorders[i].BorderThickness = isSelected ? new Thickness(3) : new Thickness(1);
        }
    }

    public DwellComboBox(
        IDwellRegistrar registrar,
        string idPrefix,
        string label,
        string[] displayOptions,
        string[] codeOptions,
        Func<string> getCode,
        Action<string> setCode,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
    {
        if (displayOptions.Length != codeOptions.Length)
        {
            throw new ArgumentException(
                "displayOptions and codeOptions must contain the same number of elements.");
        }

        _displayOptions = displayOptions;
        _codeOptions = codeOptions;
        _getCode = getCode;
        _setCode = setCode;

        _label = new DwellLabel(label);

        _panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 12)
        };

        for (int i = 0; i < displayOptions.Length; i++)
        {
            var capturedCode = codeOptions[i];
            var id = $"{idPrefix}_{i}";

            var text = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            System.Windows.Automation.AutomationProperties.SetAutomationId(
                text, $"Settings_HList_{id}");

            var btn = new DwellButton(registrar, id, "",
                () =>
                {
                    _setCode(capturedCode);
                    Refresh();
                    UpdateOptionSelectionStyles();
                },
                BtnBase, category, radiusPhysical, dwellMs,
                width: 0, height: 70);

            // Override the button's Root to use custom styling
            btn.Root.MinWidth = 150;
            btn.Root.Width = double.NaN; // Auto width
            btn.Root.Margin = new Thickness(0);
            btn.Root.Padding = new Thickness(20, 0, 20, 0);
            btn.Root.Child = text;

            var optionBorder = new Border
            {
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(6),
                BorderBrush = new SolidColorBrush(BtnBase),
                BorderThickness = new Thickness(1),
                Child = btn.Root
            };

            _options.Add(btn);
            _optionTexts.Add(text);
            _optionBorders.Add(optionBorder);
            _panel.Children.Add(optionBorder);
        }

        Refresh();
        UpdateOptionSelectionStyles();
    }

    /// <summary>Convenience overload: same display and code values.</summary>
    public DwellComboBox(
        IDwellRegistrar registrar,
        string idPrefix,
        string label,
        string[] options,
        Func<string> get,
        Action<string> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
        : this(registrar, idPrefix, label, options, options, get, set, category, radiusPhysical, dwellMs)
    {
    }

    public void Attach(Panel parent)
    {
        _label.Attach(parent);
        parent.Children.Add(_panel);
        foreach (var btn in _options)
            btn.RegisterDwell();
    }

    public void Detach(Panel parent)
    {
        _label.Detach(parent);
        parent.Children.Remove(_panel);
        foreach (var btn in _options)
            btn.UnregisterDwell();
    }

    private void Refresh()
    {
        var active = _getCode();
        for (int j = 0; j < _options.Count; j++)
        {
            bool selected = j < _codeOptions.Length && _codeOptions[j] == active;
            _options[j].Root.Background = new SolidColorBrush(selected ? SelectedColor : BtnBase);
            if (j < _optionTexts.Count)
                _optionTexts[j].Text = (selected ? "\u25C9 " : "\u25CB ") + _displayOptions[j];
        }
    }
}
