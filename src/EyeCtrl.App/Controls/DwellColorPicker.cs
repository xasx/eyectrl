namespace EyeCtrl.App.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

/// <summary>
/// A composite dwell control that presents a set of colour swatches, allowing the
/// user to select a colour by dwelling on it.
/// </summary>
/// <remarks>
/// <para>
/// Each swatch is a square <see cref="DwellButton"/> filled with the preset colour.
/// The currently selected swatch is identified by a white border; all others have a
/// transparent border. Border state is stored on the inner button's
/// <see cref="DwellControl.Root"/> and does not conflict with
/// <see cref="DwellButton.ApplyProgress"/> because <c>ApplyProgress</c> only
/// modifies the background, not the border brush.
/// </para>
/// </remarks>
public class DwellColorPicker : IDwellComposite
{
    /// <summary>
    /// The default eight-colour preset palette used when no custom presets are provided.
    /// Each entry is a (hex string, display name) pair.
    /// </summary>
    public static readonly (string Hex, string Name)[] DefaultPresets =
    [
        ("#00FF00", "Green"), ("#0088FF", "Blue"), ("#FF0000", "Red"),
        ("#FFFF00", "Yellow"), ("#00FFFF", "Cyan"), ("#FF00FF", "Magenta"),
        ("#FFFFFF", "White"), ("#FF8800", "Orange")
    ];

    private readonly DwellLabel _label;
    private readonly List<DwellButton> _swatches = new();
    private readonly List<Border> _swatchBorders = new();
    private readonly List<string> _hexValues = new();
    private readonly WrapPanel _panel;

    private readonly Func<string> _get;
    private readonly Action<string> _set;

    /// <inheritdoc/>
    public IEnumerable<DwellControl> Children => _swatches;

    /// <summary>
    /// Creates a <see cref="DwellColorPicker"/>.
    /// </summary>
    /// <param name="registrar">The dwell registrar for this overlay.</param>
    /// <param name="idPrefix">
    /// Prefix for swatch button identifiers; each swatch gets id <c>{idPrefix}_{i}</c>.
    /// </param>
    /// <param name="label">Descriptive heading displayed above the swatch panel.</param>
    /// <param name="get">Returns the currently selected colour as a hex string (e.g. <c>"#FF0000"</c>).</param>
    /// <param name="set">Applies the hex string of the selected colour.</param>
    /// <param name="category">Dwell category the swatch buttons are registered under.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels.</param>
    /// <param name="dwellMs">Dwell duration in milliseconds.</param>
    /// <param name="presets">
    /// Custom colour presets. Each entry is a (hex, name) pair.
    /// Defaults to <see cref="DefaultPresets"/> when <see langword="null"/>.
    /// </param>
    public DwellColorPicker(
        IDwellRegistrar registrar,
        string idPrefix,
        string label,
        Func<string> get,
        Action<string> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs,
        (string Hex, string Name)[]? presets = null)
    {
        _get = get;
        _set = set;
        presets ??= DefaultPresets;

        _label = new DwellLabel(label);

        _panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 12) };

        for (int i = 0; i < presets.Length; i++)
        {
            var (hex, name) = presets[i];
            var swatchId = $"{idPrefix}_{i}";
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var capturedHex = hex;

            var btn = new DwellButton(registrar, swatchId, "",
                () => { _set(capturedHex); RefreshSelection(); },
                color, category, radiusPhysical, dwellMs,
                width: 90, height: 75);

            btn.Root.Margin = new Thickness(4);
            btn.Root.CornerRadius = new CornerRadius(6);
            btn.Root.Background = new SolidColorBrush(color);
            btn.Root.BorderThickness = new Thickness(3);
            btn.Root.BorderBrush = hex.Equals(get(), StringComparison.OrdinalIgnoreCase)
                ? Brushes.White : Brushes.Transparent;
            btn.Root.ToolTip = name;
            btn.Root.Child = null; // No text label for colour swatches

            _swatches.Add(btn);
            _swatchBorders.Add(btn.Root);
            _hexValues.Add(hex);
            _panel.Children.Add(btn.Root);
        }
    }

    /// <inheritdoc/>
    public void Attach(Panel parent)
    {
        _label.Attach(parent);
        parent.Children.Add(_panel);
        foreach (var btn in _swatches)
            btn.RegisterDwell();
    }

    /// <inheritdoc/>
    public void Detach(Panel parent)
    {
        _label.Detach(parent);
        parent.Children.Remove(_panel);
        foreach (var btn in _swatches)
            btn.UnregisterDwell();
    }

    /// <summary>
    /// Updates the white selection border on the swatch that matches the current colour,
    /// clearing the border on all others.
    /// </summary>
    private void RefreshSelection()
    {
        var selected = _get();
        for (int i = 0; i < _swatchBorders.Count; i++)
        {
            _swatchBorders[i].BorderBrush = _hexValues[i].Equals(selected, StringComparison.OrdinalIgnoreCase)
                ? Brushes.White : Brushes.Transparent;
        }
    }
}
