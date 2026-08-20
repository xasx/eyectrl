namespace EyeCtrl.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Processing;

/// <summary>
/// A composite dwell control that presents a set of mutually-exclusive options as a
/// horizontal wrap panel of dwell buttons, one per option.
/// </summary>
/// <remarks>
/// <para>
/// The currently selected option is highlighted by a coloured border (green,
/// <c>SelectedColor</c>) on the outer wrapper of each button, with a thicker
/// border thickness. A radio-button symbol (◉/◯) is also prepended to each
/// option's text label.
/// </para>
/// <para>
/// Selection highlight is intentionally kept on the outer wrapper border rather
/// than the inner button background so that it does not conflict with
/// <see cref="DwellButton.ApplyProgress"/>, which overwrites the background on
/// every frame.
/// </para>
/// </remarks>
public class DwellComboBox : IDwellComposite
{
    private static Color BtnBase => Tokens.SurfaceMedium;
    private static Color SelectedColor => Tokens.ToggleOn;

    private readonly DwellLabel _label;
    private readonly List<DwellButton> _options = new();
    private readonly List<TextBlock> _optionTexts = new();
    private readonly List<Border> _optionBorders = new();
    private readonly WrapPanel _panel;

    private readonly string[] _displayOptions;
    private readonly string[] _codeOptions;
    private readonly Func<string> _getCode;
    private readonly Action<string> _setCode;

    /// <inheritdoc/>
    public IEnumerable<DwellControl> Children => _options;

    /// <summary>
    /// Creates a <see cref="DwellComboBox"/> with separate display and code values.
    /// </summary>
    /// <param name="registrar">The dwell registrar for this overlay.</param>
    /// <param name="idPrefix">
    /// Prefix for option button identifiers; each option gets id <c>{idPrefix}_{i}</c>.
    /// </param>
    /// <param name="label">Descriptive heading displayed above the option row.</param>
    /// <param name="displayOptions">
    /// Human-readable labels shown on each button. Must have the same length as
    /// <paramref name="codeOptions"/>.
    /// </param>
    /// <param name="codeOptions">
    /// Internal values passed to <paramref name="setCode"/> when an option is selected.
    /// Must have the same length as <paramref name="displayOptions"/>.
    /// </param>
    /// <param name="getCode">Returns the currently selected code value.</param>
    /// <param name="setCode">Applies the code value of the selected option.</param>
    /// <param name="category">Dwell category the option buttons are registered under.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels.</param>
    /// <param name="dwellMs">Dwell duration in milliseconds.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="displayOptions"/> and <paramref name="codeOptions"/>
    /// have different lengths.
    /// </exception>
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
            Margin = new Thickness(0, 0, 0, 24)
        };

        for (int i = 0; i < displayOptions.Length; i++)
        {
            var capturedCode = codeOptions[i];
            var id = $"{idPrefix}_{i}";

            var text = new TextBlock
            {
                Foreground = Tokens.BrushTextPrimary,
                FontSize = Tokens.FontSizeTitle,
                FontWeight = Tokens.FontWeightMedium,
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
                width: 0, height: 100);

            // Override the button's Root to use custom styling
            btn.Root.MinWidth = 150;
            btn.Root.Width = double.NaN; // Auto width
            btn.Root.Margin = new Thickness(0);
            btn.Root.Padding = new Thickness(20, 0, 20, 0);
            btn.Content = text;

            var optionBorder = new Border
            {
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = Tokens.StandardCornerRadius,
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

    /// <summary>
    /// Convenience overload for the common case where display labels and code values
    /// are identical (e.g. enum names that are also shown to the user).
    /// </summary>
    /// <param name="registrar">The dwell registrar for this overlay.</param>
    /// <param name="idPrefix">Prefix for option button identifiers.</param>
    /// <param name="label">Descriptive heading displayed above the option row.</param>
    /// <param name="options">Option values used for both display and as the code value.</param>
    /// <param name="get">Returns the currently selected value.</param>
    /// <param name="set">Applies the selected value.</param>
    /// <param name="category">Dwell category the option buttons are registered under.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels.</param>
    /// <param name="dwellMs">Dwell duration in milliseconds.</param>
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

    /// <inheritdoc/>
    public void Attach(Panel parent)
    {
        _label.Attach(parent);
        parent.Children.Add(_panel);
        foreach (var btn in _options)
            btn.RegisterDwell();
    }

    /// <inheritdoc/>
    public void Detach(Panel parent)
    {
        _label.Detach(parent);
        parent.Children.Remove(_panel);
        foreach (var btn in _options)
            btn.UnregisterDwell();
    }

    /// <summary>
    /// Updates the outer-border selection highlight for each option based on the
    /// currently selected code value. The outer border's <c>BorderBrush</c> and
    /// <c>BorderThickness</c> are used so the indicator is not overwritten by
    /// <see cref="DwellButton.ApplyProgress"/>, which only touches the inner
    /// button background.
    /// </summary>
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

    /// <summary>
    /// Updates each option's text indicator (◉ = selected, ◯ = unselected).
    /// Does not touch background colours to avoid conflicting with
    /// <see cref="DwellButton.ApplyProgress"/>; selection highlight is handled
    /// separately by <see cref="UpdateOptionSelectionStyles"/>.
    /// </summary>
    private void Refresh()
    {
        // Only update the text indicator; selection highlight is handled by UpdateOptionSelectionStyles()
        // via the outer border's BorderBrush/BorderThickness. Touching Root.Background here would
        // conflict with DwellButton.ApplyProgress() which overwrites it on every frame.
        var active = _getCode();
        for (int j = 0; j < _options.Count; j++)
        {
            bool selected = j < _codeOptions.Length && _codeOptions[j] == active;
            if (j < _optionTexts.Count)
                _optionTexts[j].Text = (selected ? "\u25C9 " : "\u25CB ") + _displayOptions[j];
        }
    }
}
