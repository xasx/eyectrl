namespace EyeCtrl.App.Views.Settings;

/// <summary>
/// Declarative description of one row on a settings page. Pure data + delegates, no WPF types:
/// <see cref="EyeCtrl.App.Views.SettingsWindow.CreateControl"/> is the single construction path that
/// maps a spec onto an <see cref="EyeCtrl.Controls.DwellControl"/>. This data shape is what a future full-MVVM
/// migration binds to (it becomes the row view-model collection); only the renderer is throwaway.
/// The `Get`/`Set`/`Text` delegates must be side-effect-free: the pagination measurer constructs
/// each row's control to measure it before rendering, so every spec's delegates run twice per
/// rebuild.
/// </summary>
public abstract record SettingsRowSpec(string Id, string Label)
{
    /// <summary>
    /// One line explaining what the setting does, drawn under <see cref="Label"/> in the row's
    /// name column. Optional: a row whose name already says everything leaves it null and the
    /// second line is not drawn at all.
    ///
    /// An <c>init</c> property rather than a positional parameter so the ~80 existing spec
    /// constructions across eleven page sources keep compiling untouched.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>A stepped numeric value with a live read/write binding to a setting.</summary>
/// <param name="Decimals">Decimal places shown (mirrors <c>DwellSlider</c>'s <c>decimals</c> arg).</param>
public sealed record SliderSpec(
    string Id, string Label, double Min, double Max, double Step, int Decimals,
    Func<double> Get, Action<double> Set) : SettingsRowSpec(Id, Label);

public sealed record ToggleSpec(
    string Id, string Label, Func<bool> Get, Action<bool> Set) : SettingsRowSpec(Id, Label);

/// <param name="Code">Value codes; <c>null</c> means the display strings are also the codes.</param>
public sealed record ComboBoxSpec(
    string Id, string Label, string[] Display, string[]? Code,
    Func<string> Get, Action<string> Set) : SettingsRowSpec(Id, Label);

/// <param name="RadiusLogical">Hit radius in logical DIP (Cursor uses the swatch radius, Selection the control radius).</param>
public sealed record ColorPickerSpec(
    string Id, string Label, Func<string> Get, Action<string> Set, double RadiusLogical)
    : SettingsRowSpec(Id, Label);

/// <summary>Non-destructive danger — repair flows; same visual family as Delete.</summary>
public enum ButtonKind { Default, Reset, Delete, Danger }

/// <summary>An action button (reset / download / delete / nav). Width/Height are per-call (they vary).</summary>
public sealed record ButtonSpec(
    string Id, string Label, Action OnActivate, ButtonKind Kind, double Width, double Height)
    : SettingsRowSpec(Id, Label);

/// <param name="Text">Deferred so dynamic labels (calibration status) re-evaluate on each rebuild.</param>
/// <param name="Wrap">
/// Wrap text wider than the content column to a second line (default) instead of clipping it —
/// status text must stay fully readable for a gaze user. The Models separator rule opts out:
/// its pinned rendering is a single line clipped at the column edge.
/// </param>
public sealed record LabelSpec(Func<string> Text, bool Wrap = true) : SettingsRowSpec("", "");

/// <summary>One page of the paginated content area; rendered top-to-bottom into <c>ContentPanel</c>.</summary>
public sealed record PageSpec(IReadOnlyList<SettingsRowSpec> Rows);
