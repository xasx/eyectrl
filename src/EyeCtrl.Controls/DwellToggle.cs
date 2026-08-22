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
    /// Horizontal room the state word reserves, and the gaps either side of it. Spelled out
    /// because <see cref="SwitchWidth"/> is derived from them below rather than guessed.
    /// </summary>
    private const double StateTextWidth = 40;
    private const double StateTextLeftGap = 16;
    private const double StateTextRightGap = 12;

    /// <summary>
    /// The switch button's box.
    ///
    /// <para>
    /// Two independent floors, and the larger wins. It may never be drawn smaller than the 100 DIP
    /// hit circle it registers (<see cref="DwellButton"/>'s WYSIWYG floor). And it has to hold its
    /// own content: <see cref="StateTextLeftGap"/> + <see cref="StateTextWidth"/> +
    /// <see cref="StateTextRightGap"/> + <see cref="TrackWidth"/> = 152 DIP. The 152 wins, and the
    /// 160 here leaves 8 DIP of slack. <see cref="DwellButton"/> clips its layout to the rounded
    /// silhouette, so a switch narrower than its content would CUT the track's edge rather than
    /// overflow visibly — nothing would throw.
    /// <c>DwellToggleVisualTests.TheSwitchIsWideEnoughForItsOwnContent</c> measures the content
    /// against this instead of restating the sum.
    /// </para>
    /// </summary>
    private const double SwitchWidth = 160;

    private readonly string _labelText;
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private readonly TextBlock _label;
    private readonly TextBlock _stateText;
    private readonly Border _rowSurface;
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
        _labelText = label;
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
            Margin = new Thickness(20, 0, 20, 0)
        };

        _stateText = new TextBlock
        {
            // TextPrimary in BOTH states, and RefreshVisual swaps no foreground — see the type doc.
            Foreground = Tokens.BrushTextPrimary,
            FontSize = Tokens.FontSizeSubtitle,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Width = StateTextWidth,
            Margin = new Thickness(StateTextLeftGap, 0, StateTextRightGap, 0)
        };

        // The UIA identity goes HERE rather than on the switch's Root, and that is a WPF
        // constraint, not a preference: a Border has no AutomationPeer, so it never enters the UIA
        // tree and an automation id on it is unreachable — FindFirstDescendant would not see it.
        // The state word is the innermost element inside the switch that CAN carry the id, and its
        // centre sits well inside the switch's hit circle, so AppFixture.SimulateDwellOn lands on
        // a point that really activates. Pinned by
        // DwellToggleVisualTests.TheUiaAnchorsCentre_FallsInsideTheSwitchesHitCircle. The id used
        // to sit on the ROW's label, which after this change is decorative — dwelling it would do
        // nothing at all.
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
            // A dark ring, so a light knob stays distinct from the light-ish ON track: the knob on
            // green alone measures 2.91:1, the ring against green 4.37 / 4.18 / 15.30.
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
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _knob
        };

        var switchContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _stateText, _track }
        };

        _switch = new DwellButton(
            registrar, id, "", Toggle, Tokens.SurfaceLight, category, radiusPhysical, dwellMs,
            width: SwitchWidth, height: 100)
        {
            Content = switchContent
        };
        // DwellButton's default (4,0,4,0) is for tiles packed side by side; this one is pinned to
        // the row's right column and the row card supplies its own inset.
        _switch.Root.Margin = new Thickness(0);

        _children = [_switch];

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(_switch.Root, 1);
        row.Children.Add(_label);
        row.Children.Add(_switch.Root);

        _rowSurface = new Border
        {
            // No explicit Width: the label column is a star, so the row takes exactly the room it
            // is given and the label wraps inside it. A fixed width would overflow the Supervisor's
            // 560 DIP setup window, which hosts two of these.
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 360,
            // 100 + the 24 DIP bottom margin is the row rhythm every other settings row carries,
            // and it is exactly what this control measured before it became a composite —
            // SettingsAutoPagination measures row heights to decide where a page breaks, so this
            // number moving re-paginates every page holding a toggle.
            Height = 100,
            Margin = new Thickness(0, 0, 0, 24),
            CornerRadius = Tokens.StandardCornerRadius,
            Background = Tokens.BrushSurfaceMedium,
            // Deliberately NO BorderThickness, matching DwellSlider's plate. A 1 DIP border sits
            // inside the 100 DIP box, leaving the switch a 98 DIP slot for its explicit 100 —
            // which WPF resolves by clipping 1 DIP off the top and bottom, and the bottom DIP is
            // the switch's 6 DIP dwell bar. A card outline is not worth shaving the dwell feedback.
            Child = row
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
            _stateText, $"{_labelText}: {_stateText.Text}");
        _trackBrush.Color = on ? Tokens.ToggleOn : Tokens.ToggleOff;
        _knobBrush.Color = on ? Tokens.TextPrimary : Tokens.TextSecondary;
        _knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}
