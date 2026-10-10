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
/// Each swatch is a <see cref="DwellButton"/> drawn as a card: its own fill is a neutral
/// surface, and its <see cref="DwellButton.Content"/> is a chip carrying the preset colour
/// above a caption naming it. The name is DRAWN rather than left to
/// <see cref="FrameworkElement.ToolTip"/>, which is set as well but reachable only by hovering
/// — and this application's user has no pointer to hover with.
/// </para>
/// <para>
/// The currently selected swatch is identified by a <see cref="Tokens.BrushTextPrimary"/>
/// border; all others carry a transparent one of the same
/// <see cref="SwatchBorderThickness"/>. Border state is stored on the inner button's
/// <see cref="DwellControl.Root"/> and does not conflict with
/// <see cref="DwellButton.ApplyProgress"/> because <c>ApplyProgress</c> no longer
/// touches <see cref="DwellControl.Root"/>'s background at all — it sweeps a haloed
/// bar (<see cref="DwellButton.ProgressBar"/>) over the fill instead, so the border
/// brush is never in contention with dwell feedback.
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
    /// default dwell colour), then green, warning and body text. The warning caption
    /// is the ROLE name, not "Amber": Daylight's warning is a dark brick red (§10.8.1),
    /// the same role-vs-colour mismatch that renamed the text preset.
    ///
    /// Derived rather than listed, so each theme offers colours from its own
    /// palette instead of Midnight's. Under Midnight this reproduces the four
    /// presets that shipped, hex for hex. Every entry is gated at >= 7:1 against
    /// its own theme's StrokeHalo by ColorPickerPresetTests — worst case across
    /// all four themes is still Midnight's Amber at 9.13:1 — because a preset
    /// that fails its own halo would collapse the §4.1.3 halo guarantee on the
    /// cursor ring. On the dark themes that bar keeps every preset light; on
    /// Daylight it keeps every preset dark — the bar is polarity-free.
    /// </summary>
    /// <param name="theme">The theme whose palette the swatches come from.</param>
    /// <returns>Hex/name pairs, accent first.</returns>
    public static (string Hex, string Name)[] PresetsFor(ITheme theme) =>
    [
        (Hex(theme.Accent), "Accent"),
        (Hex(RingGreen(theme)), "Green"),
        (Hex(theme.AccentWarning), "Warning"),
        (Hex(theme.TextPrimary), "Text"),
    ];

    /// <summary>
    /// The ring's green, which is the theme's <see cref="ITheme.AccentSuccess"/> everywhere
    /// except on Daylight — currently the only light theme, hence the type test. A second
    /// light theme must derive its own ring green; if it blindly reused AccentSuccess, the
    /// bi-tonal suite (CursorRingStyleContrastTests) would fail it, so the gap cannot open
    /// silently. Bi-tonal segments place the preset beside the halo with no
    /// band underneath, so over a mid-light backdrop (<c>#FF5C7A</c>) each family must clear
    /// 3:1 on its own — a bar no green that satisfies gate 9's chroma floor can reach (that
    /// needs L* &gt;= 31.8, this bar needs L* &lt;= 31.3; disjoint by 0.05 contrast, proven by
    /// full-sRGB enumeration in review round 1 — max clearing-green chroma is 54.28). The dark
    /// themes never hit this because their near-black halo carries the mid backdrops. So a
    /// light theme's ring green is the darkest green that still reads green — a ring
    /// decoration, not a semantic token, and free of gate 9. Gated with the rest of the
    /// presets by CursorRingStyleContrastTests and ColorPickerPresetTests.
    /// </summary>
    private static Color RingGreen(ITheme theme) =>
        theme is DaylightTheme
            ? Color.FromRgb(0x00, 0x52, 0x00)
            : theme.AccentSuccess;

    /// <summary>Swatches for the active theme. Used when no explicit preset list is passed.</summary>
    public static (string Hex, string Name)[] DefaultPresets => PresetsFor(Tokens.Current);

    /// <summary>Rendered swatch cell width in logical DIPs. See <see cref="SwatchHeight"/>.</summary>
    public const double SwatchWidth = 120;
    /// <summary>
    /// Rendered swatch cell height in logical DIPs. Together with the margins and the hit
    /// radius, these four consts are what <c>SwatchTargetGeometryTests</c> checks the
    /// non-overlap invariant against — the invariant the old hard-coded literals silently
    /// violated when the radius was shrunk below the control target floor.
    /// </summary>
    public const double SwatchHeight = 100;
    /// <summary>
    /// Horizontal gutter between swatches in the WrapPanel, in logical DIPs. 10, not 8: a
    /// 120 DIP cell plus two 10 DIP gutters puts adjacent centres 140 apart, clearing the
    /// 40 DIP settings-reachability floor that the old 8 DIP gutter (136 centre pitch,
    /// 36 clearance) fell 4 DIP short of — widened 2026-09-30 to retire the reachability
    /// gate's only clearance exemption.
    /// </summary>
    public const double SwatchHorizontalMargin = 10;
    /// <summary>Vertical gutter between wrapped swatch rows, in logical DIPs.</summary>
    public const double SwatchVerticalMargin = 20;

    /// <summary>
    /// Selection-border thickness, constant in both states so choosing a swatch cannot resize it
    /// and re-flow the WrapPanel — the same rule as DwellComboBox's OptionBorderThickness.
    ///
    /// <c>internal</c> rather than <c>public</c>, unlike the four geometry consts above: those
    /// are read from <c>EyeCtrl.App.Tests</c>, which has no InternalsVisibleTo into this
    /// assembly, whereas this one is read only from <c>EyeCtrl.Controls.Tests</c>, which does.
    /// </summary>
    internal const double SwatchBorderThickness = 3;

    private readonly DwellLabel _label;
    private readonly List<DwellButton> _swatches = new();
    private readonly List<Border> _swatchBorders = new();
    private readonly List<string> _hexValues = new();
    private readonly WrapPanel _panel;

    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly bool _showLabel;

    /// <inheritdoc/>
    public IReadOnlyList<DwellControl> Children => _swatches;

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
    /// <param name="showLabel">
    /// Whether this composite draws its own heading above its controls. False when the host
    /// lays the name out itself — the settings window's two-column rows put the name in their
    /// left column, and a second copy inside the composite would duplicate it.
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
        (string Hex, string Name)[]? presets = null,
        bool showLabel = true)
    {
        _get = get;
        _set = set;
        _showLabel = showLabel;
        presets ??= DefaultPresets;

        _label = new DwellLabel(label);

        // Same gating as DwellSlider's row, with one difference worth naming: the swatches
        // already carry SwatchVerticalMargin top AND bottom, so a swatch row is 140 DIP of
        // content on its own. Dropping the 24 here therefore changes this composite's HEIGHT
        // (164 -> 140 for a single swatch row), not merely where its content is centred.
        _panel = new WrapPanel
        {
            // The 24 DIP bottom margin is the row rhythm a SELF-LABELLED composite carries:
            // heading, controls, gap, next row. The headless form has no heading and is dropped
            // into a host row that owns the rhythm itself, so it carries none — exactly as
            // DwellToggle's headless form does. Left unconditional, the extra 24 pushed the
            // control's visual centre 12 DIP above the name it is supposed to sit beside, and
            // 12 DIP off the toggles on the same page, which never had one.
            Margin = showLabel ? new Thickness(0, 0, 0, 24) : new Thickness(0)
        };

        for (int i = 0; i < presets.Length; i++)
        {
            var (hex, name) = presets[i];
            var swatchId = $"{idPrefix}_{i}";
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var capturedHex = hex;

            // `color` is passed as baseColor but is not where the user sees it: the card fill
            // below replaces it, and the chip is what actually shows the preset. Still passed
            // rather than SurfaceMedium so the swatch degrades to a coloured button, not an
            // indistinguishable grey one, if that assignment is ever lost.
            var btn = new DwellButton(registrar, swatchId, "",
                () => { _set(capturedHex); RefreshSelection(); },
                color, category, radiusPhysical, dwellMs,
                width: SwatchWidth, height: SwatchHeight);

            btn.Root.Margin = new Thickness(
                SwatchHorizontalMargin, SwatchVerticalMargin,
                SwatchHorizontalMargin, SwatchVerticalMargin);
            // No radius override: swatches share DwellButton's Tokens.StandardCornerRadius.
            //
            // The button's own fill becomes the card behind the colour chip, so the name has a
            // surface to be legible on. The chip carries the preset colour instead. Replacing
            // Root.Background is safe: dwell feedback lives on DwellButton.ProgressBar, a
            // separate element, so it cannot be orphaned by this assignment — the invariant
            // DwellColorPickerProgressTests pins.
            btn.Root.Background = Tokens.BrushSurfaceMedium;
            btn.Root.BorderThickness = new Thickness(SwatchBorderThickness);
            btn.Root.BorderBrush = hex.Equals(get(), StringComparison.OrdinalIgnoreCase)
                ? Tokens.BrushTextPrimary : Brushes.Transparent;
            // Retained for whoever drives this with a mouse. It is not the name's affordance:
            // a tooltip needs hover, and this application's user has none — which is why the
            // caption below exists.
            btn.Root.ToolTip = name;

            var chip = new Border
            {
                Background = new SolidColorBrush(color),
                CornerRadius = Tokens.SmallCornerRadius
            };

            var caption = new TextBlock
            {
                Text = name,
                Foreground = Tokens.BrushTextSecondary,
                FontSize = Tokens.FontSizeSubtitle,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var swatchContent = new DockPanel { LastChildFill = true, Margin = new Thickness(6) };
            DockPanel.SetDock(caption, Dock.Bottom);
            swatchContent.Children.Add(caption);
            swatchContent.Children.Add(chip);

            // Content, not Root.Child: Root's child is DwellButton's own layout, and overwriting
            // it would remove the dwell progress bar with it.
            btn.Content = swatchContent;

            _swatches.Add(btn);
            _swatchBorders.Add(btn.Root);
            _hexValues.Add(hex);
            _panel.Children.Add(btn.Root);
        }
    }

    /// <inheritdoc/>
    public void Attach(Panel parent)
    {
        if (_showLabel)
            _label.Attach(parent);

        parent.Children.Add(_panel);
        foreach (var btn in _swatches)
            btn.RegisterDwell();
    }

    /// <inheritdoc/>
    public void Detach(Panel parent)
    {
        if (_showLabel)
            _label.Detach(parent);

        parent.Children.Remove(_panel);
        foreach (var btn in _swatches)
            btn.UnregisterDwell();
    }

    /// <summary>
    /// Paints the selection border on the swatch that matches the current colour and clears it
    /// on all others. Swaps the BRUSH only: the thickness stays
    /// <see cref="SwatchBorderThickness"/> in both states, because a Border's thickness lays out
    /// outside its child and a varying one would re-flow the WrapPanel on every selection.
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
