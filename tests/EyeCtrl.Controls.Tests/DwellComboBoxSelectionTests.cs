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

    /// <summary>A three-option combo over a captured selection.</summary>
    /// <param name="selected">The option that starts out selected.</param>
    private static DwellComboBox Build(string selected) =>
        Build(new FakeRegistrar(), Options, selected);

    /// <summary>A combo over <paramref name="options"/> with <paramref name="selected"/> chosen.</summary>
    private static DwellComboBox Build(string[] options, string selected) =>
        Build(new FakeRegistrar(), options, selected);

    /// <summary>
    /// A combo whose dwell activations land in <paramref name="registrar"/>, so a test can fire
    /// one the way the overlay's dwell detector would.
    /// </summary>
    private static DwellComboBox Build(FakeRegistrar registrar, string[] options, string selected)
    {
        var current = selected;
        return new DwellComboBox(
            registrar, "cb", "Mode", options,
            () => current, v => current = v,
            DwellCategory.Control, 50, 800);
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
    /// leaves the transition itself uncovered: dropping the <c>UpdateOptionSelectionStyles()</c>
    /// call from the activation lambda keeps them all green while the highlight would never move
    /// on screen. So drive a real dwell activation through the registrar and check both halves —
    /// the highlight moves, and the already-laid-out row does not.
    /// </summary>
    [StaFact]
    public void ActivatingAnOption_MovesTheHighlight_AndLeavesTheRowWhereItWas()
    {
        var registrar = new FakeRegistrar();
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

    private class FakeRegistrar : IDwellRegistrar
    {
        private readonly Dictionary<string, Action> _activations = new();

        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) =>
            _activations[id] = onActivated;

        public void Unregister(string id, DwellCategory category) => _activations.Remove(id);
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;

        /// <summary>Fires the activation registered under <paramref name="id"/>.</summary>
        public void Activate(string id) => _activations[id]();
    }
}
