namespace EyeCtrl.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Processing;

/// <summary>
/// A dwell-enabled boolean setting, drawn as a row: a plain label the user reads on the left, and
/// a switch on the right that is the only dwell target.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the row is a composite rather than one <see cref="DwellControl"/>.</b>
/// <see cref="DwellControl.UpdatePosition"/> registers the hit circle at
/// <see cref="DwellControl.Root"/>'s CENTRE, so a single-control row with a label on the left and
/// a switch on the right would put a 100 DIP circle in the middle of a ~416 DIP row: the user
/// would have to dwell the GAP between the two, and looking at the switch — the obvious thing to
/// look at — would miss by around 130 DIP. <see cref="DwellSlider"/> and
/// <see cref="DwellComboBox"/> already solve this the same way: a plain label plus separately
/// registered child targets.
/// </para>
/// <para>
/// <b>Why the label is not on the state fill.</b> The label used to be drawn on the state colour
/// itself. <c>Tokens.TextPrimary</c> #D6DEF5 on the <c>Tokens.ToggleOn</c> fill #4A8F5C measures
/// 2.91:1 — under the 3:1 large-text bar, at rest, before any dwell begins — and holding the fill
/// still through the dwell (the previous change) cannot lift a RESTING number. On the row surface
/// the same label measures 10.33 / 12.01 / 18.42 across Midnight / Ember / Contrast.
/// </para>
/// <para>
/// <b>State is never carried by colour alone.</b> The word ("On"/"Off"), the knob's side and the
/// track's fill all say it, so a colour-vision deficiency or a monochrome screen costs nothing.
/// The state word keeps ONE foreground in both states: at
/// <see cref="Tokens.FontSizeSubtitle"/> (18 DIP, normal weight) it is not WCAG large text, so it
/// owes 4.5:1, and <c>Tokens.TextSecondary</c> on the switch's <c>SurfaceLight</c> fill reaches
/// only 4.38:1 in Midnight. <c>TextPrimary</c> reaches 8.03 / 9.52 / 15.13.
/// </para>
/// <para>
/// <b>Dwell feedback</b> is the switch's own <see cref="DwellButton.ProgressBar"/>, in the
/// switch's layout row 1 — so it is never drawn over the track (row 0) and can never touch the
/// surface the label is read against. The mouse-click fallback comes from the same place:
/// <see cref="DwellButton"/> wires <c>Root.MouseLeftButtonDown</c> to its action, which is
/// <see cref="Toggle"/>.
/// </para>
/// </remarks>
public class DwellToggle : IDwellComposite
{
    private const double TrackWidth = 84;
    private const double TrackHeight = 48;
    private const double KnobSize = 34;

    /// <summary>
    /// The card's height, and therefore the switch's, and therefore the label's line budget.
    ///
    /// <para>
    /// Fixed, not merely defaulted: <c>SettingsAutoPagination</c> decides where a settings page
    /// breaks from measured row heights, so this plus the 24 DIP bottom margin is the 124 DIP every
    /// page holding a toggle is paginated on. It cannot grow, which is why an over-long label has
    /// to end somewhere — see <see cref="Label"/>'s <c>MaxHeight</c>.
    /// </para>
    /// </summary>
    private const double RowHeight = 100;

    /// <summary>Gaps either side of the state word's TEXT, inside the switch.</summary>
    private const double StateTextLeftGap = 16;
    private const double StateTextRightGap = 12;

    /// <summary>
    /// The switch's own outline, in DIPs.
    ///
    /// <para>
    /// The switch is a light card sitting on a slightly darker one, which is a 1.23:1 fill step —
    /// no boundary at all. An outline is what makes it an object, and
    /// <see cref="Tokens.BrushTextSecondary"/> is the brush that works from BOTH sides: measured,
    /// 5.63 / 6.79 / 15.04 against the row card and 4.38 / 5.38 / 12.35 against the switch's own
    /// fill, across Midnight / Ember / Contrast. <c>BorderSubtle</c> — right for the card, whose
    /// backdrop is the page — fails here on both counts (2.72 / 2.71 / 18.42 against the card,
    /// 2.12 / 2.14 / 15.13 against the fill).
    /// </para>
    /// <para>
    /// A literal rather than <see cref="Tokens.BorderThicknessStandard"/>: this is the switch's
    /// perceptibility, so it must not move with a theme's card-outline weight, and it matches the
    /// track's and the knob's rings — the three outlines inside this control are one system.
    /// </para>
    /// </summary>
    private const double SwitchOutline = 2;

    /// <summary>
    /// Gap between the switch and the card's right edge, matching the label's own 20 DIP inset on
    /// the left. At zero the switch sat flush against the card, two rounded corners overlapping.
    /// </summary>
    private const double SwitchInset = 20;

    /// <summary>
    /// The switch button's box.
    ///
    /// <para>
    /// Two independent floors, and the larger wins. It may never be drawn smaller than the 100 DIP
    /// hit circle it registers (<see cref="DwellButton"/>'s WYSIWYG floor). And it has to hold its
    /// own content, which is the state word's box — <see cref="StateTextLeftGap"/>, plus the word's
    /// text, plus the <see cref="TrackWidth"/> + <see cref="StateTextRightGap"/> its Padding
    /// reserves for the track — inside two <see cref="SwitchOutline"/> edges.
    /// <see cref="DwellButton"/> clips its layout to the rounded silhouette, so a switch narrower
    /// than its content CUTS the track's edge rather than overflowing visibly, and nothing throws.
    /// <c>DwellToggleVisualTests.TheSwitchIsWideEnoughForItsOwnContent</c> measures the real
    /// content against the real box instead of restating either sum.
    /// </para>
    /// </summary>
    private const double SwitchWidth = 160;

    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private readonly TextBlock _label;
    private readonly TextBlock _stateText;
    private readonly Border _rowSurface;
    private readonly Border _cardOutline;
    private readonly Border _track;
    private readonly Border _knob;
    private readonly DwellButton _switch;

    // Mutated in place on a state change; never reallocated.
    private readonly SolidColorBrush _trackBrush = new();
    private readonly SolidColorBrush _knobBrush = new();

    // Allocated once. Children is read by the overlay's PER-FRAME loop, so returning a fresh
    // collection expression from the getter would allocate at gaze rate.
    private readonly DwellControl[] _children;

    /// <summary>The setting's name. Plain text: it is read, never dwelled.</summary>
    internal TextBlock Label => _label;

    /// <summary>The state word inside the switch, and the element that carries the UIA identity.</summary>
    internal TextBlock StateText => _stateText;

    /// <summary>The row card both halves sit on, and the surface <see cref="Label"/> is read against.</summary>
    internal Border RowSurface => _rowSurface;

    /// <summary>
    /// The card's boundary. A separate, non-layout element rather than
    /// <see cref="RowSurface"/>'s own <c>BorderThickness</c> — see its construction for why.
    /// </summary>
    internal Border CardOutline => _cardOutline;

    /// <summary>The switch's track, whose fill carries the state.</summary>
    internal Border Track => _track;

    /// <summary>The switch's knob, whose side carries the state independently of any colour.</summary>
    internal Border Knob => _knob;

    /// <summary>The row's one dwell target.</summary>
    internal DwellButton Switch => _switch;

    /// <inheritdoc/>
    public IEnumerable<DwellControl> Children => _children;

    /// <summary>
    /// Creates a <see cref="DwellToggle"/>.
    /// </summary>
    /// <param name="registrar">The dwell registrar for this overlay.</param>
    /// <param name="id">Unique target identifier within <paramref name="category"/>.</param>
    /// <param name="label">The setting's name, shown on the row's left.</param>
    /// <param name="get">Returns the current boolean value of the setting.</param>
    /// <param name="set">Applies a new boolean value when the switch activates.</param>
    /// <param name="category">Dwell category the switch is registered under.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels.</param>
    /// <param name="dwellMs">Dwell duration in milliseconds.</param>
    public DwellToggle(
        IDwellRegistrar registrar,
        string id,
        string label,
        Func<bool> get,
        Action<bool> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
    {
        _get = get;
        _set = set;

        _label = new TextBlock
        {
            Text = label,
            Foreground = Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeTitle,
            FontWeight = Tokens.FontWeightMedium,
            VerticalAlignment = VerticalAlignment.Center,
            // Wrap, not WrapWithOverflow: Wrap breaks INSIDE a word, so a long setting name is
            // laid out over further LINES rather than cut off mid-word. It is not what keeps the
            // row inside its column — the label's STAR column is (measured: WrapWithOverflow
            // leaves the row's width unchanged, because a star column is bounded by the space the
            // Grid has and WPF clips an over-wide child to its own slot). What Wrap buys is that
            // the text is all still there to read.
            TextWrapping = TextWrapping.Wrap,
            // Truncate, do not clip. QuickActionsPageSource.cs:73 builds a ToggleSpec caption from
            // a label the USER typed, so the length is unbounded — and the card's height is not.
            // Rendered at the 416 DIP narrow column, a four-line label was cut through the middle
            // of its fourth row of glyphs: a setting whose own name is unreadable, with no scroll
            // and no hover to recover it.
            //
            // A HEIGHT bound rather than a line count. WPF's TextBlock has no MaxLines (that is
            // WinUI); given a MaxHeight it lays out as many lines as fit and applies TextTrimming
            // to the last one, so the budget follows the type ramp instead of hard-coding how many
            // lines that happens to be today. Measured under the theme font at
            // Tokens.FontSizeTitle: a line box of 31.92 DIP, so three lines occupy 95.77 of the
            // 100 available and the fourth — 127.69 in total — is what used to be bisected.
            //
            // There is no reachable home for the remainder, and that is worth stating plainly
            // rather than papering over: a ToolTip needs hover and this application's user has none
            // (the same reason the colour swatches grew visible captions), and a settings row has
            // no scroll and no expand. The full text is reachable only where the user typed it, on
            // the Quick Actions editor page — so the honest fix for a name too long to read here is
            // to shorten it there. The ellipsis at least SAYS that it was cut.
            MaxHeight = RowHeight,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(20, 0, 20, 0)
        };

        _stateText = new TextBlock
        {
            // TextPrimary in BOTH states, and RefreshVisual swaps no foreground — see the type doc.
            Foreground = Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeSubtitle,
            VerticalAlignment = VerticalAlignment.Center,
            // The word's BOX spans the whole switch, and its glyphs are placed in the room left of
            // the track by PADDING, not Margin: padding sits inside the box, so it moves the text
            // without moving the box. That is what puts the UIA anchor's centre on the switch's
            // centre — see the automation id note below — and the right padding doubles as the
            // track's reservation, so the two can never overlap.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Left,
            Padding = new Thickness(StateTextLeftGap, 0, TrackWidth + StateTextRightGap, 0)
        };

        // The UIA identity goes HERE rather than on the switch's Root, and that is a WPF
        // constraint, not a preference: a Border has no AutomationPeer, so it never enters the UIA
        // tree and an automation id on it is unreachable — FindFirstDescendant would not see it.
        // The state word is the innermost element inside the switch that CAN carry the id. The
        // mechanism is neither new nor unproven: before this control became a composite the id
        // already sat on a TextBlock nested identically under a peer-less Border and Grid, and the
        // App's UI test found it there. What changed is WHICH TextBlock.
        //
        // It used to sit on the ROW's label, which is now decorative — AppFixture.SimulateDwellOn
        // dwells an element's bounding-rect centre, and dwelling the label's would do nothing at
        // all. The word's box is stretched across the whole switch precisely so that centre IS the
        // switch's centre, rather than an offset that merely happens to fall inside the hit circle.
        // Pinned by DwellToggleVisualTests.TheUiaAnchorsCentre_FallsInsideTheSwitchesHitCircle.
        System.Windows.Automation.AutomationProperties.SetAutomationId(
            _stateText, $"Settings_Toggle_{id}");

        _knob = new Border
        {
            Width = KnobSize,
            Height = KnobSize,
            // No token expresses KnobSize / 2, and CircleCornerRadius is refused outright in
            // code-behind by CornerRadiusClampTests.EveryCircleTokenConsumer_DeclaresASquare
            // (which cannot read a runtime size and so rejects every non-/Theme/ .cs site) even
            // though this element IS square. LargeCornerRadius is the roundest legal form: a
            // rounded square rather than a disc. The compromise is purely aesthetic — the knob's
            // SIDE is what carries state, and that is what the tests assert.
            CornerRadius = Tokens.LargeCornerRadius,
            Background = _knobBrush,
            VerticalAlignment = VerticalAlignment.Center,
            // A dark ring, so a light knob stays distinct from the light-ish ON track: the knob's
            // own fill on green measures 2.91 / 3.53 / 1.37, and the ring against green
            // 4.37 / 4.18 / 15.30 — the ring is what carries it. The knob's SIDE is one of the
            // three colour-independent state carriers, so this is gated rather than recorded, by
            // the same structural-plus-cross-theme pair the track gets:
            // DwellToggleVisualTests.TheKnobIsPerceptibleAgainstTheTrackFill reads the real
            // brushes, Theme.ToggleContrastTests.TheKnob_IsPerceptibleAgainstEitherTrackFill
            // sweeps every palette.
            BorderThickness = new Thickness(2),
            BorderBrush = Tokens.BrushSurfaceDark
        };

        _track = new Border
        {
            Width = TrackWidth,
            Height = TrackHeight,
            // As on the knob: no token gives a stadium at this size (a true one wants
            // TrackHeight / 2 = 24), CircleCornerRadius would clamp to an ELLIPSE on a non-square
            // element anyway and is refused in code-behind besides. LargeCornerRadius is the
            // roundest legal form. Documented compromise, aesthetic only.
            CornerRadius = Tokens.LargeCornerRadius,
            Background = _trackBrush,
            // The track's perceptibility must NOT depend on its fill. Against the switch's own
            // SurfaceLight fill the state colours measure ToggleOn 2.76 / 2.70 / 11.03 and
            // ToggleOff 1.23 / 1.25 / 1.39 — so the ON track fails the 3:1 non-text-component bar
            // in two of three themes, and the OFF track is invisible in all three: the switch
            // would have no visible extent at all when off. TextSecondary measures
            // 4.38 / 5.38 / 12.35 against SurfaceLight and clears 3:1 in every theme, in both
            // states. Gated structurally by
            // DwellToggleVisualTests.TheTrackIsPerceptibleAgainstTheSwitchFill and across themes
            // by Theme.ToggleContrastTests.TheTrack_IsPerceptibleAgainstTheSwitchFill.
            BorderThickness = new Thickness(2),
            BorderBrush = Tokens.BrushTextSecondary,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = _knob
        };

        // ONE cell, not a horizontal StackPanel: the state word has to span the switch for its UIA
        // bounding rect to be centred on it, and a StackPanel would size the word to its text. The
        // track is right-aligned over the same cell, and the word's right Padding reserves exactly
        // the track's width, so the two cannot overlap — if a longer word ever outgrew that
        // reservation the cell's desired width would exceed the switch and
        // TheSwitchIsWideEnoughForItsOwnContent would fail.
        var switchContent = new Grid();
        switchContent.Children.Add(_stateText);
        switchContent.Children.Add(_track);

        _switch = new DwellButton(
            registrar, id, "", Toggle, Tokens.SurfaceLight, category, radiusPhysical, dwellMs,
            width: SwitchWidth, height: RowHeight)
        {
            Content = switchContent
        };
        // Outline the switch. Without it a SurfaceLight card on a SurfaceMedium one is a 1.23:1
        // fill step, so the thing the user aims at has no edge; and unlike the row card the switch
        // cannot borrow BorderSubtle for the job — see SwitchOutline for both sets of numbers.
        // This costs the dwell bar nothing, because the bar lives INSIDE this button's own two-row
        // layout and simply moves in with the border, exactly as DwellColorPicker's 3 DIP swatch
        // border already does on a Root of the same explicit 100 DIP height
        // (DwellColorPicker.cs:169).
        _switch.Root.BorderThickness = new Thickness(SwitchOutline);
        _switch.Root.BorderBrush = Tokens.BrushTextSecondary;
        // DwellButton's default (4,0,4,0) is for tiles packed side by side. This one is pinned to
        // the row's right column, and the card carries no Padding of its own, so the inset from
        // the card's right edge has to come from here.
        _switch.Root.Margin = new Thickness(0, 0, SwitchInset, 0);

        _children = [_switch];

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(_switch.Root, 1);
        row.Children.Add(_label);
        row.Children.Add(_switch.Root);

        // The card's boundary, drawn as a SIBLING of the row rather than as the card's own
        // BorderThickness — and that is a layout decision, not a stylistic one.
        //
        // A Border deflates its child by its BorderThickness, so a bordered 100 DIP card hands the
        // switch a 98 DIP slot for its explicit 100, and WPF does not split the shortfall:
        // ComputeAlignmentOffset demotes Stretch to Top, the switch is pushed DOWN, and all of it
        // lands on the bottom edge — where the 6 DIP dwell bar is. Two measured costs followed: a
        // layout clip of 0;0;160;98 at BorderThicknessStandard 1 and 0;0;160;96 at 2, breaking the
        // WYSIWYG floor in every theme; and, once the shortfall reached 2 DIP, a quarter of the bar
        // itself (602 Accent pixels over 4 rows down to 455 over 3, in the Contrast theme — the one
        // built for low vision).
        //
        // A negative margin on the row cancelled the inset and fixed both, but pushed the switch
        // one DIP OUTSIDE the card's border line: rendered, the switch's own outline visibly poked
        // out of the row and broke the card's line where the two met. An IsHitTestVisible = false
        // outline in a wrapping Grid consumes no layout at all, so nothing has to be pushed
        // anywhere — the switch stays fully inside the card and the card's line stays continuous
        // over it. Raising the card's height was the third option and is barred: it would move the
        // measured 124 DIP row and re-paginate every settings page holding a toggle.
        //
        // The fill stays on RowSurface, so "the surface the label is read against" is still one
        // element. Only the boundary moved. Gated by
        // DwellToggleVisualTests.TheCardsBoundaryConsumesNoLayout.
        _cardOutline = new Border
        {
            CornerRadius = Tokens.StandardCornerRadius,
            BorderThickness = new Thickness(Tokens.BorderThicknessStandard),
            BorderBrush = Tokens.BrushBorderSubtle,
            IsHitTestVisible = false
        };

        // Outline last, so it paints OVER the switch's top and bottom edges where the two
        // coincide, and the card reads as one continuous shape.
        var cardContent = new Grid();
        cardContent.Children.Add(row);
        cardContent.Children.Add(_cardOutline);

        _rowSurface = new Border
        {
            // No explicit Width: the label column is a star, so the row takes exactly the room it
            // is given and the label wraps inside it. A fixed width would overflow the Supervisor's
            // 560 DIP setup window, which hosts two of these.
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 360,
            // RowHeight + the 24 DIP bottom margin is the row rhythm every other settings row
            // carries, and it is exactly what this control measured before it became a composite.
            Height = RowHeight,
            Margin = new Thickness(0, 0, 0, 24),
            CornerRadius = Tokens.StandardCornerRadius,
            Background = Tokens.BrushSurfaceMedium,
            // The fill only. Palette rule 1 puts the boundary on the border — "the border
            // carries the boundary; the fill only suggests depth",
            // docs/design/overlay-unification-spec.md:284-286, gated by
            // tools/palette_validate.py:352-365 — and the numbers agree: SurfaceMedium on the
            // page's SurfaceDark is a 1.23:1 step, while BorderSubtle on it measures
            // 3.35 / 3.32 / 21.00. So there IS a border; it is _cardOutline above, drawn without
            // consuming layout. Deleting it outright (which one commit did, to dodge the inset)
            // took the row from a delineated object — the old full-bleed ToggleOn fill read 4.37:1
            // against the page — down to that 1.23:1 nothing.
            Child = cardContent
        };

        RefreshVisual();
    }

    /// <inheritdoc/>
    public void Attach(Panel parent)
    {
        parent.Children.Add(_rowSurface);
        _switch.RegisterDwell();
    }

    /// <inheritdoc/>
    public void Detach(Panel parent)
    {
        parent.Children.Remove(_rowSurface);
        _switch.UnregisterDwell();
    }

    /// <summary>
    /// Flips the setting and refreshes the switch. The switch's action, so both a completed dwell
    /// and the <see cref="DwellButton"/> mouse fallback arrive here.
    /// </summary>
    internal void Toggle()
    {
        _set(!_get());
        RefreshVisual();
    }

    /// <summary>
    /// Re-reads the setting and repaints the switch. User-rate, not gaze-rate: the two brushes are
    /// mutated in place rather than replaced.
    /// </summary>
    private void RefreshVisual()
    {
        var on = _get();
        _stateText.Text = on ? "On" : "Off";
        // The switch has no text of its own, and a Border carries no UIA name, so the accessible
        // name has to be composed here: the setting the user is looking at, plus its state.
        System.Windows.Automation.AutomationProperties.SetName(
            _stateText, $"{_label.Text}: {_stateText.Text}");
        _trackBrush.Color = on ? Tokens.ToggleOn : Tokens.ToggleOff;
        _knobBrush.Color = on ? Tokens.TextPrimary : Tokens.TextSecondary;
        _knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}
