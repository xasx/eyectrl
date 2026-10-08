namespace EyeCtrl.Controls.Tests;

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// Selecting an option must not change any option's size. The original code swapped a 1px
/// border for a 3px one, and a Border's thickness sits outside its child — so the chosen pill
/// grew 4 DIP in both axes and the WrapPanel re-laid the whole row. Options jumped under the
/// user's gaze, and every hit circle in the row moved with them.
/// </summary>
public class DwellComboBoxSelectionTests
{
    /// <summary>Options for the colour tests, which do not measure anything.</summary>
    private static readonly string[] Options = ["Alpha", "Beta", "Gamma"];

    /// <summary>
    /// Labels short enough that every option's inner button clamps to its <c>MinWidth</c> of
    /// 150 DIP, so a measured row is driven by the border alone. "Gamma" does not clamp. Recorded
    /// inner-button widths from an unconstrained Measure of this composite under
    /// <see cref="Tokens.FontStandard"/>: each of these three is 150.000 in every selection
    /// state, where "Gamma" is 151.570 unselected and 152.050 selected — the ◉ it wears when
    /// selected is 0.480 DIP wider than the ◯ it wears otherwise. Two width sources that have
    /// nothing to do with the border under test. (The wrapper each button sits in measures
    /// 12 DIP more than its button: 2 DIP of border per side plus an 8 DIP right/bottom margin.)
    /// </summary>
    private static readonly string[] NarrowOptions = ["Low", "Mid", "High"];

    /// <summary>
    /// The host's unmatched-marker format (T02: the host owns the wording). Deliberately not
    /// today's shipped English, so the assertions below fail if the control ignores the host's
    /// format and renders a built-in of its own.
    /// </summary>
    private const string MarkerFormat = "out of range: {0}";

    /// <summary>A three-option combo over a captured selection.</summary>
    /// <param name="selected">The option that starts out selected.</param>
    private static DwellComboBox Build(string selected) =>
        Build(new FakeDwellRegistrar(), Options, selected);

    /// <summary>A combo over <paramref name="options"/> with <paramref name="selected"/> chosen.</summary>
    private static DwellComboBox Build(string[] options, string selected) =>
        Build(new FakeDwellRegistrar(), options, selected);

    /// <summary>
    /// A combo whose dwell activations land in <paramref name="registrar"/>, so a test can fire
    /// one the way the overlay's dwell detector would.
    /// </summary>
    private static DwellComboBox Build(FakeDwellRegistrar registrar, string[] options, string selected)
    {
        var current = selected;
        return new DwellComboBox(
            registrar, "cb", "Mode", options,
            () => current, v => current = v,
            DwellCategory.Control, 50, 800, MarkerFormat);
    }

    /// <summary>
    /// Attaches <paramref name="combo"/> to a fresh scratch panel and measures it unconstrained.
    /// The theme font is pinned on the panel because the recorded DIP figures on
    /// <see cref="NarrowOptions"/> only reproduce under it, not under WPF's default face.
    /// </summary>
    private static StackPanel AttachAndMeasure(DwellComboBox combo)
    {
        var panel = new StackPanel();
        TextElement.SetFontFamily(panel, Tokens.FontStandard);
        combo.Attach(panel);
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return panel;
    }

    /// <summary>The wrapper border each option's button sits in — the element under test.</summary>
    private static IReadOnlyList<Border> OptionWrappers(DwellComboBox combo) =>
        combo.Children.Select(c => (Border)c.Root.Parent).ToList();

    private static IReadOnlyList<Color> OutlineColours(DwellComboBox combo) =>
        OptionWrappers(combo).Select(b => ((SolidColorBrush)b.BorderBrush).Color).ToList();

    [StaFact]
    public void EveryOption_HasTheSameBorderThickness_RegardlessOfSelection()
    {
        var combo = Build("Alpha");

        var thicknesses = OptionWrappers(combo).Select(b => b.BorderThickness).ToList();

        Check.That(thicknesses.Select(t => t.Left).Distinct().Count()).IsEqualTo(1);
        Check.That(thicknesses.First().Left).IsEqualTo(2.0);
    }

    /// <summary>
    /// Selection is carried by colour, and the green must land on the option that is actually
    /// selected. Counting one green somewhere in the row would pass with the highlight on the
    /// wrong pill, so assert its index; the middle case is there so a fencepost error cannot
    /// hide at index 0.
    /// </summary>
    [StaTheory]
    [InlineData("Alpha", 0)]
    [InlineData("Beta", 1)]
    public void TheGreenOutline_LandsOnTheSelectedOption(string selected, int selectedIndex)
    {
        var combo = Build(selected);

        var colours = OutlineColours(combo);

        Check.WithCustomMessage(
                $"'{selected}' is selected, so option {selectedIndex} must outline in ToggleOn "
                + $"({Tokens.ToggleOn}); the row paints [{string.Join(", ", colours)}]")
            .That(colours[selectedIndex]).IsEqualTo(Tokens.ToggleOn);

        for (int i = 0; i < colours.Count; i++)
        {
            if (i == selectedIndex)
                continue;

            // Named, not merely "some other colour": the unselected outline regressing to the
            // button's own fill (SurfaceMedium) would still count as distinct from ToggleOn while
            // being invisible against the thing it outlines.
            Check.WithCustomMessage(
                    $"option {i} ({Options[i]}) is unselected, so it must outline in BorderSubtle "
                    + $"({Tokens.BorderSubtle}); the row paints [{string.Join(", ", colours)}]")
                .That(colours[i]).IsEqualTo(Tokens.BorderSubtle);
        }

        // The premise the check above rests on, asserted rather than assumed. A code regression
        // to the button's fill fails above, not here; what this catches is the theme side — an
        // author setting these two tokens equal would leave every assertion above passing while
        // the unselected outline vanished into the fill it outlines.
        Check.WithCustomMessage(
                "BorderSubtle equals the button's own fill SurfaceMedium "
                + $"({Tokens.SurfaceMedium}) — an outline in it would be invisible")
            .That(Tokens.BorderSubtle).IsNotEqualTo(Tokens.SurfaceMedium);
    }

    /// <summary>
    /// The tests above assert thickness and colour, which are only the mechanism. This one
    /// measures the thing the user actually feels: an option must occupy exactly the same space
    /// whether or not it is the selected one, so nothing moves under the gaze.
    ///
    /// Measured over <see cref="NarrowOptions"/> so the border is the only thing that can move
    /// the geometry; a label wide enough to burst the button's MinWidth, or the ◉/◯
    /// advance-width difference, would otherwise show up here as a wobble unrelated to this
    /// defect.
    /// </summary>
    [StaFact]
    public void ChangingWhichOptionIsSelected_DoesNotChangeTheRowGeometry()
    {
        var firstSelected = Build(NarrowOptions, "Low");
        var lastSelected = Build(NarrowOptions, "High");

        AttachAndMeasure(firstSelected);
        AttachAndMeasure(lastSelected);

        // The assertion that bites: compare each option against ITSELF in the two selection
        // states. A pill that grows when chosen slides every pill after it — and every hit
        // circle with them — under the user's gaze. Note that comparing the two rows' totals
        // instead would prove nothing: a WrapPanel's sum is order-blind, so it is identical
        // whichever single option grew.
        var withFirstSelected = OptionWrappers(firstSelected);
        var withLastSelected = OptionWrappers(lastSelected);
        for (int i = 0; i < withFirstSelected.Count; i++)
        {
            Check.WithCustomMessage(
                    $"option {i} ({NarrowOptions[i]}) measures {withFirstSelected[i].DesiredSize} "
                    + $"when the first option is selected but {withLastSelected[i].DesiredSize} "
                    + "when the last is — selecting an option resizes it.")
                .That(withFirstSelected[i].DesiredSize).IsEqualTo(withLastSelected[i].DesiredSize);
        }

        // What the per-option comparison above cannot see: a size difference that depends on an
        // option's index rather than on its selection — styling only the first option, say —
        // which is identical in both rows and so cancels out there. Under NarrowOptions every
        // label clamps to the same width, so a uniform row is the correct expectation.
        foreach (var (combo, which) in new[] { (firstSelected, "first"), (lastSelected, "last") })
        {
            var sizes = OptionWrappers(combo).Select(b => b.DesiredSize).ToList();
            Check.WithCustomMessage(
                    $"with the {which} option selected the options measure "
                    + $"{string.Join(", ", sizes)} — this row is not uniform.")
                .That(sizes.Distinct().Count()).IsEqualTo(1);
        }
    }

    /// <summary>
    /// Every other test here inspects a combo built with its selection already in place, which
    /// leaves the transition itself uncovered: dropping the <c>Refresh()</c>
    /// call from the activation lambda keeps them all green while the highlight would never move
    /// on screen. So drive a real dwell activation through the registrar and check both halves —
    /// the highlight moves, and the already-laid-out row does not.
    /// </summary>
    [StaFact]
    public void ActivatingAnOption_MovesTheHighlight_AndLeavesTheRowWhereItWas()
    {
        var registrar = new FakeDwellRegistrar();
        var combo = Build(registrar, NarrowOptions, "Low");
        var panel = AttachAndMeasure(combo);

        var before = OptionWrappers(combo).Select(b => b.DesiredSize).ToList();
        Check.That(OutlineColours(combo)[0]).IsEqualTo(Tokens.ToggleOn);

        // Fire the dwell activation the composite registered for the third option, as the
        // overlay's dwell detector would once the user held their gaze on it.
        registrar.Activate("cb_2");

        var after = OutlineColours(combo);
        Check.WithCustomMessage(
                $"after activating the third option the row paints [{string.Join(", ", after)}] — "
                + $"the highlight did not move to it (ToggleOn is {Tokens.ToggleOn})")
            .That(after[2]).IsEqualTo(Tokens.ToggleOn);
        Check.WithCustomMessage(
                $"the option that was selected before is still outlined {after[0]} — "
                + "two options look selected at once")
            .That(after[0]).IsEqualTo(Tokens.BorderSubtle);

        // Re-measure the same tree. Measure() short-circuits on any element still flagged valid,
        // and this tree is detached, so it gets no layout pass to walk the dirty flag up from the
        // wrapper the activation touched. Invalidate the path explicitly — without this the
        // re-measure hands back cached sizes and cannot see a re-flow at all (verified by
        // mutation: the 1px/3px defect slipped through).
        foreach (var wrapper in OptionWrappers(combo))
        {
            wrapper.InvalidateMeasure();
            ((FrameworkElement)wrapper.Parent).InvalidateMeasure();
        }

        panel.InvalidateMeasure();
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wrappers = OptionWrappers(combo);
        for (int i = 0; i < wrappers.Count; i++)
        {
            Check.WithCustomMessage(
                    $"option {i} ({NarrowOptions[i]}) measured {before[i]} before the activation "
                    + $"and {wrappers[i].DesiredSize} after — the row moved under the user's gaze.")
                .That(wrappers[i].DesiredSize).IsEqualTo(before[i]);
        }
    }

    // ────── Whose label is it: the composite's, or the host's? ──────

    /// <summary>
    /// A combo that draws no heading of its own — the form a two-column settings row hosts.
    /// </summary>
    private static DwellComboBox BuildHeadless()
    {
        var current = "Alpha";
        return new DwellComboBox(
            new FakeDwellRegistrar(), "cb", "Mode", Options,
            () => current, v => current = v,
            DwellCategory.Control, 50, 800, MarkerFormat, showLabel: false);
    }

    /// <summary>
    /// A two-column settings row lays the setting's NAME out itself, in its left column, so the
    /// composite it hosts must attach its controls and nothing else. A heading of its own would
    /// put the same name on screen twice.
    ///
    /// <para>
    /// Asserted over the HOST panel's own children: the heading is the only <c>TextBlock</c> this
    /// composite ever parents to its host (each option's caption lives inside the WrapPanel's
    /// buttons), so an empty top-level set is exactly "it attached no heading of its own".
    /// </para>
    /// </summary>
    [StaFact]
    public void WithShowLabelFalse_TheCompositeAttachesNoLabelOfItsOwn()
    {
        var panel = new StackPanel();

        BuildHeadless().Attach(panel);

        var headings = panel.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
        Check.WithCustomMessage(
                $"the composite attached {headings.Count} heading(s) of its own "
                + $"([{string.Join(", ", headings)}]) despite showLabel: false. The row that hosts "
                + "it has already drawn the name in its left column.")
            .That(panel.Children.OfType<TextBlock>()).IsEmpty();
    }

    /// <summary>
    /// The other direction, and the one that protects every existing call site: the flag defaults
    /// to <c>true</c>, so a composite nobody passed it to still draws its own heading exactly as
    /// before.
    /// </summary>
    [StaFact]
    public void ByDefault_TheCompositeStillAttachesItsOwnLabel()
    {
        var panel = new StackPanel();

        Build("Alpha").Attach(panel);

        Check.WithCustomMessage(
                "the default form must still draw its own heading — SettingsWindow and every "
                + "other caller passes no flag and expects the row it always got.")
            .That(panel.Children.OfType<TextBlock>().Single().Text).IsEqualTo("Mode");
    }

    // ────── A stored code that matches no option ──────

    /// <summary>
    /// The panel the option wrappers live in, reached through a wrapper rather than a field,
    /// so the test sees the same tree the user does.
    /// </summary>
    private static WrapPanel OptionPanel(DwellComboBox combo) =>
        (WrapPanel)OptionWrappers(combo)[0].Parent;

    /// <summary>
    /// The one panel child that is not an option wrapper: the unmatched-value marker. Asserted
    /// to be exactly one rather than picked with <c>First()</c>, because "no marker at all" is
    /// the defect this suite must be able to see — an exception from an empty sequence names
    /// nothing, and a second non-option child would make every assertion below ambiguous.
    /// </summary>
    private static Border UnmatchedMarker(DwellComboBox combo)
    {
        var wrappers = OptionWrappers(combo);
        var extras = OptionPanel(combo).Children.OfType<Border>()
            .Where(b => !wrappers.Contains(b)).ToList();

        Check.WithCustomMessage(
                $"the combo panel holds {extras.Count} non-option border(s); exactly one "
                + "unmatched-value marker is expected (visible or collapsed). A stored code "
                + "matching no option used to render as nothing selected.")
            .That(extras).HasSize(1);

        return extras[0];
    }

    /// <summary>The marker's caption — the panel it draws the stored value on.</summary>
    private static TextBlock MarkerLabel(DwellComboBox combo) =>
        (TextBlock)((Border)UnmatchedMarker(combo).Child).Child;

    /// <summary>The ◉/◯ caption of option <paramref name="index"/>.</summary>
    private static string OptionText(DwellComboBox combo, int index) =>
        ((TextBlock)((DwellButton)combo.Children[index]).Content!).Text;

    /// <summary>
    /// The user's own state, in the user's own values: settings name <c>"360M"</c>, the models
    /// folder holds only <c>"1.5B"</c>, so the row offers <c>["Off", "1.5B"]</c>. Every option
    /// renders unselected — as it must, because no option IS selected — and the stored value
    /// itself is what gets marked, as an unavailable entry, so the row stops being silent about
    /// a value that is configured.
    /// </summary>
    [StaFact]
    public void StoredCodeMatchingNoOption_MarksNoOption_AndShowsTheStoredValueAsUnavailable()
    {
        var combo = Build(["Off", "1.5B"], "360M");

        var colours = OutlineColours(combo);
        Check.WithCustomMessage(
                $"'360M' matches no option, so no option may wear the selected outline; the row "
                + $"paints [{string.Join(", ", colours)}] (ToggleOn is {Tokens.ToggleOn})")
            .That(colours).ContainsOnlyElementsThatMatch(c => c == Tokens.BorderSubtle);

        foreach (var (text, i) in new[] { (OptionText(combo, 0), 0), (OptionText(combo, 1), 1) })
        {
            Check.WithCustomMessage(
                    $"option {i} reads '{text}' — it wears the ◉ selected glyph while 360M "
                    + "matches nothing, so two things would claim to be selected.")
                .That(text.StartsWith("\u25C9")).IsFalse();
        }

        Check.WithCustomMessage(
                "the stored value 360M is not named anywhere in the row, so a gaze user has no "
                + "way to see what is configured.")
            .That(MarkerLabel(combo).Text).Contains("360M");
        Check.WithCustomMessage(
                "the marker names 360M but does not say it is unavailable, so it reads as a "
                + "third live option.")
            .That(MarkerLabel(combo).Text).Contains("out of range:");
        Check.That(UnmatchedMarker(combo).Visibility).IsEqualTo(Visibility.Visible);
    }

    /// <summary>
    /// The normal case, and the reason the fix cannot be "mark everything": a stored code that
    /// IS an option must render exactly as it always did — green outline and ◉ on that option —
    /// with the marker gone. This is the test that reddens if the fix treats a valid id as
    /// stale, and it also reddens if the marker is simply always visible.
    /// </summary>
    [StaFact]
    public void StoredCodeMatchingAnOption_MarksIt_AndHidesTheUnmatchedMarker()
    {
        var combo = Build(["Off", "1.5B"], "1.5B");

        Check.WithCustomMessage(
                $"'1.5B' is one of the options, so it must be outlined in ToggleOn "
                + $"({Tokens.ToggleOn}); the row paints "
                + $"[{string.Join(", ", OutlineColours(combo))}]")
            .That(OutlineColours(combo)[1]).IsEqualTo(Tokens.ToggleOn);
        Check.That(OptionText(combo, 1)).StartsWith("\u25C9 ");
        Check.That(OptionText(combo, 0)).StartsWith("\u25CB ");
        Check.WithCustomMessage(
                "the unmatched marker is on screen while the stored value matches an option, "
                + "so the row claims a value is unavailable when it is not.")
            .That(UnmatchedMarker(combo).Visibility).IsEqualTo(Visibility.Collapsed);
    }

    /// <summary>
    /// The empty-value branch, which nothing observed until review: an empty stored code matches
    /// no option either, so without the guard it renders the marker with an empty value — a
    /// warning pill naming nothing (the host's format with <c>{0}</c> blank). The guard keeps
    /// that branch silent.
    ///
    /// <para>
    /// The positive control runs FIRST and in the same test, because the assertion below is a
    /// negative one: without it, this test would also pass on a combo that never shows a marker
    /// at all — the absence of evidence wearing a test's name.
    /// </para>
    /// <para>
    /// The guard is a trade, not a truth: for <c>k_layout</c> an empty <c>ActiveLayout</c> IS the
    /// silent row this ticket exists to abolish. It is kept because a marker with no value in it
    /// tells the user less, and because fixing the empty-layout row belongs in that getter.
    /// </para>
    /// </summary>
    /// <param name="stored">The unset stored value: empty, or only whitespace.</param>
    [StaTheory]
    [InlineData("")]
    [InlineData("   ")]
    public void StoredCodeThatIsEmpty_ShowsNoMarker_WhileARealStoredValueStillDoes(string stored)
    {
        var real = Build(["Off", "1.5B"], "360M");
        Check.WithCustomMessage(
                "a real stored value matching no option must show the marker — without this "
                + "control the assertion below would pass on a combo that never shows one.")
            .That(UnmatchedMarker(real).Visibility).IsEqualTo(Visibility.Visible);

        var empty = Build(["Off", "1.5B"], stored);
        Check.WithCustomMessage(
                $"the stored value is '{stored}', which is not a value at all — a marker would "
                + "name nothing, telling the user less than the silent row does. The marker must "
                + "stay collapsed and the glyphs must claim nothing.")
            .That(UnmatchedMarker(empty).Visibility).IsEqualTo(Visibility.Collapsed);
        Check.That(OptionText(empty, 0)).StartsWith("\u25CB ");
        Check.That(OptionText(empty, 1)).StartsWith("\u25CB ");
    }

    /// <summary>
    /// The marker is a statement, not a choice: it must add no dwell target and no
    /// <c>Children</c> entry. A registered target that activates nothing is worse than no
    /// target — a gaze user completes a dwell, gets the feedback, and nothing changes.
    /// </summary>
    [StaFact]
    public void StoredCodeMatchingNoOption_AddsNoDwellTarget()
    {
        var registrar = new FakeDwellRegistrar();
        var combo = Build(registrar, ["Off", "1.5B"], "360M");

        combo.Attach(new StackPanel());

        Check.That(combo.Children).HasSize(2);
        Check.WithCustomMessage(
                $"the registrar holds [{string.Join(", ", registrar.Registered)}]; the options "
                + "are the only things on this row a dwell may complete on.")
            .That(registrar.Registered).ContainsExactly("cb_0", "cb_1");
    }

    /// <summary>
    /// The state is carried by the text and the glyph, and this test holds it there.
    ///
    /// <para>
    /// In the Contrast theme the marker's outline cannot be told from an unselected option's by
    /// any token the palette has — <c>AccentWarning</c> vs <c>BorderSubtle</c> is 1.07:1, and
    /// <c>AccentWarning</c> vs <c>ToggleOn</c> 1.28:1 — and under protanopia hue does not rescue
    /// a 1.07 either. So the warning glyph and the words are not decoration: they ARE the
    /// marker, and a "tidy-up" that reduced it to a coloured pill would satisfy every
    /// contrast-over-surface gate while making the row say nothing at all. This test is what
    /// fails in that world.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheUnmatchedMarker_CarriesTheStateInItsTextNotItsColour()
    {
        var combo = Build(["Off", "1.5B"], "360M");

        var markerText = MarkerLabel(combo).Text;
        Check.WithCustomMessage(
                $"the marker reads '{markerText}'. Its glyph must be neither ◉ nor ◯ — those two "
                + "are the option vocabulary, and a marker wearing one of them would read as an "
                + "option in a state rather than as the stored value the options do not contain.")
            .That(markerText.StartsWith("\u25C9") || markerText.StartsWith("\u25CB")).IsFalse();
        Check.WithCustomMessage(
                $"the marker reads '{markerText}', which does not name the stored value — the "
                + "one thing the row previously failed to show.")
            .That(markerText).Contains("360M");
        Check.WithCustomMessage(
                $"the marker reads '{markerText}', which never says the value is unavailable — "
                + "the state the user has to be told.")
            .That(markerText).Contains("out of range:");
    }

    /// <summary>
    /// The marker's appearance is linked to the palette sweep in
    /// <c>Theme.ComboBoxUnmatchedMarkerContrastTests</c> by VALUE, exactly as the slider
    /// steppers' outline is: the sweep measures <c>AccentWarning</c> against the page and the
    /// marker fill, and it cannot fail on a code change. Repointing the marker at another
    /// token — or dropping the outline entirely — leaves that sweep measuring a pairing the
    /// control does not use. The same file records the palette fact that makes the TEXT the
    /// carrier: no token separates the outline from both option states in Contrast, so the
    /// colour is a redundant channel and the glyph and wording are load-bearing.
    /// </summary>
    [StaFact]
    public void TheUnmatchedMarker_NamesItsBrushAndThickness()
    {
        var combo = Build(["Off", "1.5B"], "360M");
        var marker = UnmatchedMarker(combo);
        var option = OptionWrappers(combo)[0];

        Check.WithCustomMessage(
                $"the marker's outline is {marker.BorderThickness} where an option's is "
                + $"{option.BorderThickness} — the row's state marks must not differ in weight.")
            .That(marker.BorderThickness).IsEqualTo(option.BorderThickness);

        Check.WithCustomMessage(
                $"the marker's outline is {marker.BorderBrush}, not AccentWarning "
                + $"({Tokens.AccentWarning}). Theme.ComboBoxUnmatchedMarkerContrastTests measures "
                + "AccentWarning against SurfaceDark and SurfaceMedium; any other token leaves "
                + "that sweep green over a marker the user cannot see.")
            .That(((SolidColorBrush)marker.BorderBrush).Color).IsEqualTo(Tokens.AccentWarning);

        var fill = (Border)marker.Child;
        Check.WithCustomMessage(
                $"the marker's fill is {fill.Background}, not SurfaceMedium "
                + $"({Tokens.SurfaceMedium}) — the fill the sweep pairs the outline and the label "
                + "against.")
            .That(((SolidColorBrush)fill.Background).Color).IsEqualTo(Tokens.SurfaceMedium);

        Check.That(((SolidColorBrush)MarkerLabel(combo).Foreground).Color)
            .IsEqualTo(Tokens.TextPrimary);
    }

    /// <summary>
    /// The marker tracks the live selection, not just the construction-time one: activating an
    /// option after an unmatched start must hide the marker and move the highlight in the same
    /// pass. This is the repair path the user takes — dwell the Qwen entry — and it is the
    /// reason the marker is refreshed from the activation lambda rather than only in the
    /// constructor.
    /// </summary>
    [StaFact]
    public void ActivatingAnOption_AfterAnUnmatchedStart_HidesTheMarker_AndMarksTheOption()
    {
        var registrar = new FakeDwellRegistrar();
        var combo = Build(registrar, ["Off", "1.5B"], "360M");
        AttachAndMeasure(combo);
        Check.That(UnmatchedMarker(combo).Visibility).IsEqualTo(Visibility.Visible);

        registrar.Activate("cb_1");

        Check.WithCustomMessage(
                "after selecting 1.5B the unmatched marker is still on screen — the row keeps "
                + "saying a value is unavailable after the user has just replaced it.")
            .That(UnmatchedMarker(combo).Visibility).IsEqualTo(Visibility.Collapsed);
        Check.That(OutlineColours(combo)[1]).IsEqualTo(Tokens.ToggleOn);
        Check.That(OptionText(combo, 1)).StartsWith("\u25C9 ");
        Check.That(OutlineColours(combo)[0]).IsEqualTo(Tokens.BorderSubtle);
    }

    /// <summary>
    /// <c>Detach</c> has to undo whatever <c>Attach</c> did, in either form. Both halves are
    /// asserted: the element count BEFORE the detach (or an emptiness check would pass on a
    /// composite that attached nothing at all), and zero after.
    ///
    /// <para>
    /// <b>What this test does and does not falsify.</b> The <c>showLabel: false</c> direction is
    /// NOT falsifiable, and saying so is the point: measured, ungating <c>Detach</c> so it always
    /// removes the heading leaves this theory green in both forms, because
    /// <c>UIElementCollection.Remove</c> of an element that was never parented is a silent no-op —
    /// it neither throws nor disturbs the panel's other children. So the branch this documents
    /// cannot be killed by any assertion over the host panel. Its real value is the pre-detach
    /// count and the <c>showLabel: true</c> direction, where the removal IS observable. The gate
    /// stays in the source for symmetry with <c>Attach</c>, whose own gate is observable and IS
    /// killed by <see cref="WithShowLabelFalse_TheCompositeAttachesNoLabelOfItsOwn"/> — and
    /// because the no-op is a framework tolerance for the never-parented case only: a label
    /// parented to a DIFFERENT panel is a case WPF does throw on.
    /// </para>
    /// </summary>
    /// <param name="showLabel">Which form to attach.</param>
    /// <param name="attachedElements">
    /// What that form parents to its host: the heading plus the option row, or the option row
    /// alone.
    /// </param>
    [StaTheory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public void Detach_LeavesTheHostPanelAsItFoundIt_InEitherLabelMode(
        bool showLabel, int attachedElements)
    {
        var panel = new StackPanel();
        var combo = showLabel ? Build("Alpha") : BuildHeadless();

        combo.Attach(panel);
        Check.WithCustomMessage(
                $"with showLabel: {showLabel} the composite parented {panel.Children.Count} "
                + $"element(s) to its host, not {attachedElements}.")
            .That(panel.Children.Count).IsEqualTo(attachedElements);

        combo.Detach(panel);

        Check.WithCustomMessage(
                "Detach left " + panel.Children.Count + " element(s) behind. A settings page "
                + "rebuilds its rows, so anything left over is drawn twice.")
            .That(panel.Children.Count).IsEqualTo(0);
    }

}
