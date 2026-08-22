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
    /// <summary>
    /// Labels short enough that every option clamps to the pill's <c>MinWidth</c> of 150 DIP,
    /// so a measured row is driven by the border alone — each of these pills measures exactly
    /// 150.000 DIP wide in every selection state. "Gamma" does not clamp: at the title font size
    /// it measures 151.570 unselected and 152.050 selected, because the ◉ it wears when selected
    /// is 0.480 DIP wider than the ◯ it wears otherwise. Two width sources that have nothing to
    /// do with the border under test. (Figures from a recorded unconstrained Measure of this
    /// composite under <see cref="Tokens.FontStandard"/>.)
    /// </summary>
    private static readonly string[] NarrowOptions = ["Low", "Mid", "High"];

    /// <summary>A three-option combo over a captured selection.</summary>
    /// <param name="selected">The option that starts out selected.</param>
    private static DwellComboBox Build(string selected) =>
        Build(["Alpha", "Beta", "Gamma"], selected);

    /// <summary>A combo over <paramref name="options"/> with <paramref name="selected"/> chosen.</summary>
    private static DwellComboBox Build(string[] options, string selected)
    {
        var current = selected;
        return new DwellComboBox(
            new FakeRegistrar(), "cb", "Mode", options,
            () => current, v => current = v,
            DwellCategory.Control, 50, 800);
    }

    /// <summary>
    /// Attaches <paramref name="combo"/> to a fresh scratch panel and measures it unconstrained.
    /// The font family is pinned on the panel so every combo measures under the same font.
    /// </summary>
    private static StackPanel AttachAndMeasure(DwellComboBox combo)
    {
        var panel = new StackPanel();
        TextElement.SetFontFamily(panel, Tokens.FontStandard);
        combo.Attach(panel);
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return panel;
    }

    private static IReadOnlyList<Border> OptionBorders(DwellComboBox combo) =>
        combo.Children.Select(c => (Border)c.Root.Parent).ToList();

    [StaFact]
    public void EveryOption_HasTheSameBorderThickness_RegardlessOfSelection()
    {
        var combo = Build("Alpha");

        var thicknesses = OptionBorders(combo).Select(b => b.BorderThickness).ToList();

        Check.That(thicknesses.Select(t => t.Left).Distinct().Count()).IsEqualTo(1);
        Check.That(thicknesses.First().Left).IsEqualTo(2.0);
    }

    [StaFact]
    public void SelectionIsCarriedByColour_NotByThickness()
    {
        var combo = Build("Alpha");

        var borders = OptionBorders(combo);

        // Compare COLOURS, not brush instances. SolidColorBrush inherits DependencyObject's
        // reference equality — every option gets its own `new SolidColorBrush(...)`, so
        // Distinct() over the brushes themselves counts three, not two, however they are painted.
        var colours = borders.Select(b => ((SolidColorBrush)b.BorderBrush).Color).ToList();
        Check.That(colours.Distinct().Count()).IsEqualTo(2);
        Check.That(colours.Count(c => c == Tokens.ToggleOn)).IsEqualTo(1);
        Check.That(borders.Select(b => b.BorderThickness.Left).Distinct().Count()).IsEqualTo(1);

        // Which two colours, named. Counting distinct colours cannot see the unselected brush
        // regress to the button's own fill (SurfaceMedium) — an outline the same colour as the
        // thing it outlines is no outline at all, yet it stays just as "distinct" from ToggleOn.
        // These are the post-construction brushes: the constructor's initial BorderBrush is
        // unobservable because UpdateOptionSelectionStyles overwrites every one of them.
        Check.WithCustomMessage(
                $"unselected options must outline in BorderSubtle ({Tokens.BorderSubtle}); the row "
                + $"paints [{string.Join(", ", colours)}]")
            .That(colours.Count(c => c == Tokens.BorderSubtle)).IsEqualTo(2);
        Check.WithCustomMessage(
                $"an option is outlined in the button's own fill SurfaceMedium ({Tokens.SurfaceMedium}) "
                + "— an invisible outline")
            .That(colours.Any(c => c == Tokens.SurfaceMedium)).IsFalse();
    }

    /// <summary>
    /// The two tests above assert thickness identity, which is only a proxy. This one measures
    /// the thing the user actually feels: two identical rows differing only in which option is
    /// selected must occupy exactly the same space, and within one row every option pill must be
    /// the same size — so nothing moves under the gaze when a selection changes.
    ///
    /// Measured over <see cref="NarrowOptions"/> so the border is the only thing that can move
    /// the geometry; a label wide enough to burst the pill's MinWidth, or the ◉/◯ advance-width
    /// difference, would otherwise show up here as a sub-DIP wobble unrelated to this defect.
    /// </summary>
    [StaFact]
    public void ChangingWhichOptionIsSelected_DoesNotChangeTheRowGeometry()
    {
        var firstSelected = Build(NarrowOptions, "Low");
        var lastSelected = Build(NarrowOptions, "High");

        var firstPanel = AttachAndMeasure(firstSelected);
        var lastPanel = AttachAndMeasure(lastSelected);

        // COARSE BACKSTOP, NOT THE REAL CHECK. A WrapPanel's total is order-blind: whichever
        // option grew, the sum is the same, so this passed with the 1px/3px defect live. It can
        // only see the row's overall footprint changing (e.g. a thickness that varies with how
        // many options exist) — never which option grew. The per-option self-comparison below is
        // the assertion that bites.
        Check.WithCustomMessage(
                $"the row's total footprint is {firstPanel.DesiredSize} with the first option "
                + $"selected but {lastPanel.DesiredSize} with the last — order-blind, so this says "
                + "nothing about which option moved; see the per-option check below for that.")
            .That(firstPanel.DesiredSize).IsEqualTo(lastPanel.DesiredSize);

        // The assertion that bites: compare each option against ITSELF in the two selection
        // states. A pill that grows when chosen slides every pill after it — and every hit
        // circle with them — under the user's gaze.
        var firstPills = OptionBorders(firstSelected);
        var lastPills = OptionBorders(lastSelected);
        for (int i = 0; i < firstPills.Count; i++)
        {
            Check.WithCustomMessage(
                    $"option {i} ({NarrowOptions[i]}) measures {firstPills[i].DesiredSize} when the "
                    + $"first option is selected but {lastPills[i].DesiredSize} when the last is — "
                    + "selecting an option resizes it.")
                .That(firstPills[i].DesiredSize).IsEqualTo(lastPills[i].DesiredSize);
        }

        foreach (var (combo, which) in new[]
                 {
                     (firstSelected, "first"), (lastSelected, "last")
                 })
        {
            var sizes = OptionBorders(combo).Select(b => b.DesiredSize).ToList();
            Check.WithCustomMessage(
                    $"with the {which} option selected the option pills measure "
                    + $"{string.Join(", ", sizes)} — the selected one is a different size.")
                .That(sizes.Distinct().Count()).IsEqualTo(1);
        }
    }

    private class FakeRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
