namespace EyeCtrl.App.Views.Settings;

using EyeCtrl.Core.ActionMenu;

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

/// <param name="Code">Value codes; <c>null</c> means the display strings are also the codes.
/// The code is the value persisted in settings JSON and is never translated — a combo passes an
/// explicit array even when it currently equals <paramref name="Display"/>, so that translating
/// the display later cannot change what is saved. Pinned by <c>ComboCodeIdentityTests</c>.</param>
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

/// <summary>
/// The Action Menu tab's jump-to-preview row: a nav button that takes the user straight to the
/// preview page however many toggle pages the tab has grown to.
///
/// <para>
/// A distinct kind rather than a plain <see cref="ButtonSpec"/> because it opts into the ring
/// preview's spec-level omission: when the budget cannot show the ring, a "View ring preview"
/// row that lands on a page with no preview is a dead affordance, so the shell drops the two
/// together (<c>SettingsAutoPagination.FitRingPreview</c>). It renders exactly like a ButtonSpec
/// row.
/// </para>
/// </summary>
public sealed record RingJumpSpec(
    string Id, string Label, Action OnActivate, ButtonKind Kind, double Width, double Height)
    : SettingsRowSpec(Id, Label);

/// <summary>
/// The ring preview: the Action Menu tab's read-only miniature of the runtime ring's first
/// layer, rendered by the same painter and plan the runtime uses, with ZERO dwell targets.
///
/// <para>
/// Pure data + delegates, like every spec: <see cref="Plan"/> builds the same
/// <see cref="RingPlan"/> the runtime window builds (visible-list order, live CircleSize), and
/// <see cref="Scale"/> is the fit scalar the shell stamps before measure — one value driving
/// both the <c>LayoutTransform</c> and the omission decision. It is the one spec kind that opts
/// into that omission: a preview that cannot fit is dropped before chunking rather than
/// clipped.
/// </para>
/// </summary>
/// <param name="Id">The spec's id. Inert for dwell registration — the preview registers nothing.</param>
/// <param name="Plan">Builds the plan the painting and the fit scalar read. Side-effect-free.</param>
/// <param name="CaptionNotAControl">The "not a control" honesty caption, drawn beside the ring.</param>
/// <param name="CaptionLabels">The labels-are-dwell-only honesty caption, drawn beside the ring.</param>
/// <param name="Scale">
/// The uniform fit scalar from <see cref="RingPlan.ScaleFor"/>, stamped by the shell before
/// measure. The default is true size for consumers that never stamp (tests, gates).
/// </param>
/// <param name="MoreAnnotationFormat">
/// The host's "<c>{label} +{count}</c>" composer for the More slot; null means the English
/// format. The shell passes it through to the painter, which owns no text of its own.
/// </param>
public sealed record RingPreviewSpec(
    string Id,
    Func<RingPlan> Plan,
    string CaptionNotAControl,
    string CaptionLabels,
    double Scale = 1.0,
    Func<string, int, string>? MoreAnnotationFormat = null) : SettingsRowSpec(Id, Label: "");

/// <param name="Text">Deferred so dynamic labels (calibration status) re-evaluate on each rebuild.</param>
/// <param name="Wrap">
/// Wrap text wider than the content column to a second line (default) instead of clipping it —
/// status text must stay fully readable for a gaze user. Rule-like labels (a run of one glyph)
/// must clip at the column edge, not stack into a block of strokes.
/// </param>
public sealed record LabelSpec(Func<string> Text, bool Wrap = true) : SettingsRowSpec("", "");

/// <summary>
/// A full-width hairline rule separating sections within one tab's content. Renders as a
/// 1 px BrushBorderSubtle border stretching the content column (the '─'-character label it
/// replaced rendered at its text width and stopped mid-column).
/// </summary>
public sealed record SeparatorSpec() : SettingsRowSpec("", "");

/// <summary>One page of the paginated content area; rendered top-to-bottom into <c>ContentPanel</c>.</summary>
public sealed record PageSpec(IReadOnlyList<SettingsRowSpec> Rows);
