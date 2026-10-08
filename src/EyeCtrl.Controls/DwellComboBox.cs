namespace EyeCtrl.Controls;

using System.Collections.Generic;
using System.Globalization;
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
/// Each option's button sits inside an outer wrapper border. Every wrapper carries an outline
/// of the same constant thickness; what marks the selected option is the colour of that outline
/// — green (<see cref="Tokens.ToggleOn"/>) on the selected one, <see cref="Tokens.BorderSubtle"/>
/// on the rest. Only the brush ever changes, never the thickness, so selecting an option cannot
/// resize it or re-flow the row. A radio-button symbol (◉/◯) is also prepended to each option's
/// text label.
/// </para>
/// <para>
/// Selection highlight is intentionally kept on the outer wrapper border rather than the
/// inner button background. <see cref="DwellButton.ApplyProgress"/> no longer writes that
/// background — it sweeps <see cref="DwellButton.ProgressBar"/> — but the separation still
/// matters: selection is persistent state, dwell progress is transient, and keeping them on
/// different elements is what stops either from clobbering the other.
/// </para>
/// <para>
/// A stored code that matches no option — the Active Model combo naming a model whose files were
/// deleted, a layout name from an older build, a hand-edited settings.json — used to render as a
/// row with NO option selected and nothing on screen to explain it. The composite now names the
/// stored value in an unmatched-value marker appended after the options: a pill reading
/// "⚠ {value} (not available)", outlined in <see cref="Tokens.AccentWarning"/>. It is a
/// statement, not a choice — it registers no dwell target — and it is shown only while no option
/// matches, so it can never contradict the selection highlight.
/// </para>
/// <para>
/// The state is carried by the WARNING GLYPH and the WORDS, not by the outline colour. In the
/// Contrast theme no token separates an outline from both the selected and the unselected option
/// outlines — <c>AccentWarning</c> is 1.07:1 from <c>BorderSubtle</c> and 1.28:1 from
/// <c>ToggleOn</c> there — so colour is a redundant channel for normal vision and never the only
/// one. Both facts are asserted by Theme.ComboBoxUnmatchedMarkerContrastTests, and the glyph and
/// wording are pinned by DwellComboBoxSelectionTests.TheUnmatchedMarker_CarriesTheStateInItsTextNotItsColour.
/// </para>
/// <para>
/// Reach, for the record: the thirteen other combo call sites keep the marker collapsed and
/// therefore pay only three WPF objects per construction — no layout space, identical rendering —
/// until a stored value no option matches appears, which is the generalisation this composite is
/// supposed to offer. Where one can (k_layout and k_lang read stored values that a hand-edited
/// settings.json or a build that renamed a value can leave invalid), the marker is exactly the
/// state the user has to be shown.
/// </para>
/// </remarks>
public class DwellComboBox : IDwellComposite
{
    private static Color BtnBase => Tokens.SurfaceMedium;

    /// <summary>
    /// Option border thickness, identical selected or not.
    ///
    /// Constant by design: a Border's thickness lays out around its child, so varying it moved
    /// the pill and re-flowed the whole WrapPanel on every selection.
    ///
    /// Deliberately a literal and not <see cref="Tokens.BorderThicknessStandard"/> (1 under
    /// Midnight and Ember, 2 under Contrast): a themed thickness would make this a per-theme
    /// variable, and a thickness that does not vary is the entire point. Two rather than one so
    /// the unselected outline carries against both surfaces it meets — the settings page behind
    /// it (<see cref="Tokens.SurfaceDark"/>, the window background of SettingsWindow) and the
    /// button's own <c>BtnBase</c> fill inside it — without the selected state needing to grow
    /// to be seen.
    /// </summary>
    private const double OptionBorderThickness = 2;

    private readonly DwellLabel _label;
    private readonly List<DwellButton> _options = new();
    private readonly List<TextBlock> _optionTexts = new();
    private readonly List<Border> _optionBorders = new();
    private readonly WrapPanel _panel;

    /// <summary>
    /// The unmatched-value marker: the pill that names a stored code no option matches, so the
    /// row never renders as "nothing configured" without saying what is configured. Added to
    /// <see cref="_panel"/> after the options and collapsed unless
    /// <see cref="Refresh"/> finds no match. Deliberately NOT a <see cref="DwellButton"/> and
    /// never registered: it states the current value, so completing a dwell on it would promise
    /// a change it cannot make.
    /// </summary>
    private readonly Border _unmatchedMarker;
    private readonly TextBlock _unmatchedText;

    private readonly string[] _displayOptions;
    private readonly string[] _codeOptions;
    private readonly Func<string> _getCode;
    private readonly Action<string> _setCode;
    private readonly bool _showLabel;

    /// <summary>
    /// The host's format for the unmatched-value marker, <c>{0}</c> being the stored code.
    /// The library owns no wording of its own (T02); today the settings host passes
    /// "⚠ {0} (not available)", and T07 replaces it with a key.
    /// </summary>
    private readonly string _notAvailableFormat;

    /// <inheritdoc/>
    public IReadOnlyList<DwellControl> Children => _options;

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
    /// <param name="notAvailableFormat">
    /// The host's text for the unmatched-value marker: a format whose <c>{0}</c> is the stored
    /// code. Required, so the control carries no built-in wording.
    /// </param>
    /// <param name="showLabel">
    /// Whether this composite draws its own heading above its controls. False when the host
    /// lays the name out itself — the settings window's two-column rows put the name in their
    /// left column, and a second copy inside the composite would duplicate it.
    /// </param>
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
        int dwellMs,
        string notAvailableFormat,
        bool showLabel = true)
    {
        ArgumentNullException.ThrowIfNull(notAvailableFormat);
        if (displayOptions.Length != codeOptions.Length)
        {
            throw new ArgumentException(
                "displayOptions and codeOptions must contain the same number of elements.");
        }

        _displayOptions = displayOptions;
        _codeOptions = codeOptions;
        _getCode = getCode;
        _setCode = setCode;
        _showLabel = showLabel;
        _notAvailableFormat = notAvailableFormat;

        _label = new DwellLabel(label);

        _panel = new WrapPanel
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
                // Sane default only: Refresh() repaints every wrapper at
                // the end of this constructor and on every selection change. Kept so a refactor
                // that stops calling it cannot leave a row of invisible borders behind.
                BorderBrush = Tokens.BrushBorderSubtle,
                BorderThickness = new Thickness(OptionBorderThickness),
                Child = btn.Root
            };

            _options.Add(btn);
            _optionTexts.Add(text);
            _optionBorders.Add(optionBorder);
            _panel.Children.Add(optionBorder);
        }

        // The unmatched-value marker, built from the same chrome as an option — same fill, same
        // 100 DIP box, same 2 DIP outline — so it reads as part of the row rather than as a
        // caption floating beside it. Only the outline's colour and the label differ, and both
        // are pinned against the palette by Theme.ComboBoxUnmatchedMarkerContrastTests. The
        // fill Border mirrors DwellButton.Root's geometry exactly (DwellButton applies the same
        // MinWidth/Height/Padding/CornerRadius to its own Root in this constructor).
        _unmatchedText = new TextBlock
        {
            Foreground = Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeTitle,
            FontWeight = Tokens.FontWeightMedium,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            // Wraps for the same reason an option's caption does: a long stored id must stay
            // inside the content column rather than push the pill past the card edge, where its
            // text would be drawn over pixels the user cannot see. The WrapPanel wraps the pill
            // to the next line when it cannot fit beside the options.
            TextWrapping = TextWrapping.Wrap
        };

        var unmatchedFill = new Border
        {
            Background = Tokens.BrushSurfaceMedium,
            CornerRadius = Tokens.StandardCornerRadius,
            MinWidth = 150,
            Height = 100,
            Padding = new Thickness(20, 0, 20, 0),
            Child = _unmatchedText
        };

        _unmatchedMarker = new Border
        {
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = Tokens.StandardCornerRadius,
            BorderBrush = Tokens.BrushAccentWarning,
            BorderThickness = new Thickness(OptionBorderThickness),
            Child = unmatchedFill,
            Visibility = Visibility.Collapsed
        };
        _panel.Children.Add(_unmatchedMarker);

        Refresh();
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
    /// <param name="notAvailableFormat">
    /// The host's text for the unmatched-value marker: a format whose <c>{0}</c> is the stored
    /// code. Required, so the control carries no built-in wording.
    /// </param>
    /// <param name="showLabel">
    /// Whether this composite draws its own heading above its controls. False when the host
    /// lays the name out itself — the settings window's two-column rows put the name in their
    /// left column, and a second copy inside the composite would duplicate it.
    /// </param>
    public DwellComboBox(
        IDwellRegistrar registrar,
        string idPrefix,
        string label,
        string[] options,
        Func<string> get,
        Action<string> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs,
        string notAvailableFormat,
        bool showLabel = true)
        : this(registrar, idPrefix, label, options, options, get, set, category, radiusPhysical,
               dwellMs, notAvailableFormat, showLabel)
    {
    }

    /// <inheritdoc/>
    public void Attach(Panel parent)
    {
        if (_showLabel)
            _label.Attach(parent);

        parent.Children.Add(_panel);
        foreach (var btn in _options)
            btn.RegisterDwell();
    }

    /// <inheritdoc/>
    public void Detach(Panel parent)
    {
        if (_showLabel)
            _label.Detach(parent);

        parent.Children.Remove(_panel);
        foreach (var btn in _options)
            btn.UnregisterDwell();
    }

    /// <summary>
    /// The index of the option whose code equals <paramref name="code"/>, or -1 when none does.
    /// The ONE matching rule on this composite: <see cref="Refresh"/> calls it once and feeds
    /// every visual mark from the single result, so the outline colour, the ◉/◯ glyph and the
    /// unmatched-value marker cannot disagree about what is selected. (They used to be matched in
    /// two places against two separate reads of <c>_getCode()</c> — the shape that lets a fix
    /// reach one site and miss the other.)
    /// </summary>
    private int IndexOfOption(string code)
    {
        for (int i = 0; i < _codeOptions.Length; i++)
        {
            if (_codeOptions[i] == code)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Repaints every visual that carries selection state, from one match: each option wrapper's
    /// <c>BorderBrush</c>, each option's ◉/◯ glyph, and the unmatched-value marker. Only brushes
    /// and text are written, never the wrapper thickness — see
    /// <see cref="OptionBorderThickness"/>.
    /// </summary>
    /// <remarks>
    /// The selection highlight sits on the wrapper rather than on the button itself so that it
    /// cannot be clobbered by dwell feedback, which <see cref="DwellButton.ApplyProgress"/>
    /// renders as <see cref="DwellButton.ProgressBar"/> inside the button. The glyph write does
    /// not touch backgrounds; <c>Root.Background</c> is <see cref="DwellButton"/>'s own brush
    /// instance — replacing it here is what used to orphan the colour swatches' dwell feedback
    /// (fixed 2026-08-20), so leave it to the button.
    /// </remarks>
    private void Refresh()
    {
        var storedCode = _getCode();
        var selectedIndex = IndexOfOption(storedCode);

        for (int i = 0; i < _optionBorders.Count && i < _codeOptions.Length; i++)
        {
            _optionBorders[i].BorderBrush =
                i == selectedIndex ? Tokens.BrushToggleOn : Tokens.BrushBorderSubtle;
        }

        for (int j = 0; j < _options.Count && j < _optionTexts.Count; j++)
        {
            bool selected = j == selectedIndex;
            _optionTexts[j].Text = (selected ? "\u25C9 " : "\u25CB ") + _displayOptions[j];
        }

        // The empty-code guard keeps a combo whose value has not been set from rendering
        // "⚠  (not available)" — a warning pill naming nothing, which tells the user less than
        // the silent row it would replace. The trade is narrower than "an empty value is nothing
        // configured": for k_layout an empty ActiveLayout IS the silent row this whole change
        // exists to abolish (the keyboard runs on its own fallback while the combo names
        // nothing), and the guard leaves that row silent. It is kept because a marker with no
        // value in it is worse, not because the empty row is right — if k_layout's empty row is
        // ever fixed, the fix is a default in its getter, not this guard. Pinned by
        // DwellComboBoxSelectionTests.StoredCodeThatIsEmpty_ShowsNoMarker_WhileARealStoredValueStillDoes.
        var unmatched = selectedIndex < 0 && !string.IsNullOrWhiteSpace(storedCode);
        _unmatchedMarker.Visibility = unmatched ? Visibility.Visible : Visibility.Collapsed;
        if (unmatched)
        {
            // The stored code is a plain string; the invariant provider only keeps CA1305 quiet
            // and cannot change the rendered text.
            _unmatchedText.Text = string.Format(
                CultureInfo.InvariantCulture, _notAvailableFormat, storedCode);
        }
    }
}
