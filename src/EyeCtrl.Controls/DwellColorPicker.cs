namespace EyeCtrl.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
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
    /// Palette-aligned presets, Accent first. Every entry must clear 7:1 against
    /// Tokens.StrokeHalo — see ColorPickerPresetTests. This list feeds
    /// CursorSettings.CircleColor, which colours a haloed ring on a transparent
    /// overlay, so a dark or mid-tone entry collapses the 4.1.3 guarantee.
    ///
    /// AccentDanger (#FF5C7A) is deliberately absent: it fails that bar at 6.62:1,
    /// and is reserved for destructive actions rather than cursor decoration.
    /// </summary>
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// The swatches offered for <paramref name="theme"/>: its accent first (the
    /// default dwell colour), then success, warning and body text.
    ///
    /// Derived rather than listed, so each theme offers colours from its own
    /// palette instead of Midnight's. Under Midnight this reproduces the four
    /// presets that shipped, hex for hex. Every entry is gated at >= 7:1 against
    /// its own theme's StrokeHalo by ColorPickerPresetTests — worst case across all
    /// three themes is 9.13:1 — because a dark preset would collapse the §4.1.3
    /// halo guarantee on the cursor ring.
    /// </summary>
    /// <param name="theme">The theme whose palette the swatches come from.</param>
    /// <returns>Hex/name pairs, accent first.</returns>
    public static (string Hex, string Name)[] PresetsFor(ITheme theme) =>
    [
        (Hex(theme.Accent), "Accent"),
        (Hex(theme.AccentSuccess), "Green"),
        (Hex(theme.AccentWarning), "Amber"),
        (Hex(theme.TextPrimary), "Light"),
    ];

    /// <summary>Swatches for the active theme. Used when no explicit preset list is passed.</summary>
    public static (string Hex, string Name)[] DefaultPresets => PresetsFor(Tokens.Current);

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
            // No radius override: swatches now share DwellButton's Tokens.StandardCornerRadius.
            btn.Root.Background = new SolidColorBrush(color);
            btn.Root.BorderThickness = new Thickness(3);
            btn.Root.BorderBrush = hex.Equals(get(), StringComparison.OrdinalIgnoreCase)
                ? Tokens.BrushTextPrimary : Brushes.Transparent;
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
                ? Tokens.BrushTextPrimary : Brushes.Transparent;
        }
    }
}
