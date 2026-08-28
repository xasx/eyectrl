namespace EyeCtrl.Controls.Tests;

using System;
using System.Collections.Generic;
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
/// The value plate must say what a bare number cannot: where this value sits between its
/// minimum and maximum. A gaze user dwelling "+" otherwise has no way to know they are at an end.
/// </summary>
public class DwellSliderPlateTests
{
    /// <summary>A slider over [min, max] reporting a fixed current value.</summary>
    /// <param name="min">The range's lower end.</param>
    /// <param name="max">The range's upper end.</param>
    /// <param name="step">Amount one activation moves the value.</param>
    /// <param name="current">The value the slider reports.</param>
    /// <param name="decimals">Decimal places shown; 1 unless a case needs integers.</param>
    private static DwellSlider Build(
        double min, double max, double step, double current, int decimals = 1) =>
        new(new FakeDwellRegistrar(), "sl", "Smoothing", min, max, step, decimals,
            () => current, _ => { }, DwellCategory.Control, 50, 800);

    /// <summary>
    /// Attaches <paramref name="slider"/> to a scratch panel inside a generously sized host and
    /// runs a real layout pass, so <c>ActualWidth</c> and <c>DesiredSize</c> mean something. The
    /// theme font is pinned because <c>SettingsWindow</c> is an <c>OverlayWindow</c>, which sets
    /// it and lets it inherit down — measuring in WPF's default face would report widths the
    /// application never produces.
    /// </summary>
    /// <param name="slider">The composite to attach and lay out.</param>
    private static void LayOut(DwellSlider slider)
    {
        var panel = new StackPanel();
        TextElement.SetFontFamily(panel, Tokens.FontStandard);
        slider.Attach(panel);

        var host = new Border { Child = panel, Width = 600, Height = 400 };
        host.Measure(new Size(600, 400));
        host.Arrange(new Rect(0, 0, 600, 400));
        host.UpdateLayout();
    }

    /// <summary>
    /// Attaches <paramref name="slider"/> without laying it out — enough to register its dwell
    /// targets, which <c>Attach</c> does and the constructor does not.
    /// </summary>
    /// <param name="slider">The composite to attach.</param>
    private static void AttachOnly(DwellSlider slider) => slider.Attach(new StackPanel());

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Of(Brush brush) => ((SolidColorBrush)brush).Color;

    [StaFact]
    public void Plate_ShowsTheValueAndBothEndsOfTheRange()
    {
        var slider = Build(0, 1, 0.1, current: 0.5);

        Check.That(slider.ValueText.Text).IsEqualTo("0.5");
        Check.That(slider.MinimumText.Text).IsEqualTo("0.0");
        Check.That(slider.MaximumText.Text).IsEqualTo("1.0");
    }

    [StaFact]
    public void Rail_FillFraction_TracksWhereTheValueSitsInTheRange()
    {
        var slider = Build(0, 1, 0.1, current: 0.5);

        // Exact today, but a computed quotient: IsCloseTo so the next person copying this line
        // for a range with a nonzero min gets a real assertion rather than a flake.
        Check.That(slider.RailFraction).IsCloseTo(0.5, 1e-9);
    }

    [StaFact]
    public void Rail_AtTheMinimum_IsEmpty_AndAtTheMaximum_IsFull()
    {
        Check.That(Build(1, 6, 0.5, current: 1.0).RailFraction).IsCloseTo(0.0, 1e-9);
        Check.That(Build(1, 6, 0.5, current: 6.0).RailFraction).IsCloseTo(1.0, 1e-9);
    }

    [StaFact]
    public void Rail_OnADegenerateRange_DoesNotDivideByZero()
    {
        // min == max is nonsense but must not produce NaN and paint an invisible rail.
        var slider = Build(3, 3, 1, current: 3.0, decimals: 0);

        Check.That(double.IsNaN(slider.RailFraction)).IsFalse();
        Check.That(slider.RailFraction).IsEqualTo(0.0);
    }

    /// <summary>
    /// The four tests above only read state the constructor left behind, so every one of them
    /// stays green if <c>UpdateRail</c> is never called from <c>Adjust</c> — the plate would
    /// then be a frozen picture of the value the page opened with, which is worse than no rail
    /// at all. So drive a real dwell activation through the registrar, the way the overlay's
    /// dwell detector does once the user has held their gaze on "+", and check that BOTH halves
    /// of the plate moved: the number and the rail. (Same technique as
    /// <see cref="DwellComboBoxSelectionTests.ActivatingAnOption_MovesTheHighlight_AndLeavesTheRowWhereItWas"/>.)
    /// </summary>
    [StaFact]
    public void ActivatingPlus_MovesBothTheNumberAndTheRail()
    {
        var registrar = new FakeDwellRegistrar();
        var value = 0.5;
        var slider = new DwellSlider(
            registrar, "sl", "Smoothing", 0, 1, 0.1, 1,
            () => value, v => value = v, DwellCategory.Control, 50, 800);

        // Attach, because registration happens there and not in the constructor: without it the
        // activation the registrar fires below does not exist. No layout pass — this test reads
        // only the number and the scale factor, neither of which needs one.
        AttachOnly(slider);

        Check.That(slider.ValueText.Text).IsEqualTo("0.5");
        Check.That(slider.RailFraction).IsCloseTo(0.5, 1e-9);

        // "_up" is the plus button; DwellSlider registers {idPrefix}_dn and {idPrefix}_up.
        registrar.Activate("sl_up");

        Check.WithCustomMessage(
                $"after one activation of '+' the plate reads '{slider.ValueText.Text}' — the "
                + "number did not follow the value")
            .That(slider.ValueText.Text).IsEqualTo("0.6");
        Check.WithCustomMessage(
                $"the value moved to 0.6 of the way along [0, 1] but the rail is at "
                + $"{slider.RailFraction} — the number moved and the rail did not, so the plate "
                + "now contradicts itself.")
            .That(slider.RailFraction).IsCloseTo(0.6, 1e-9);
    }

    /// <summary>
    /// <see cref="DwellSlider.RailFraction"/> reads <c>ScaleX</c>, and a <c>ScaleX</c> of 0.5
    /// means "half as wide" whatever it is anchored to — a <c>RenderTransformOrigin</c> of
    /// (0.5, 0) leaves every assertion above green while the fill grows outwards from the rail's
    /// centre, reading as "the middle half of the range" rather than "up to here". So measure
    /// the swept extent after a real layout pass, as
    /// <see cref="DwellButtonProgressTests.ApplyProgress_SweepsFromTheLeftEdge"/> does for the
    /// dwell bar: the fill's left edge must still be the rail's left edge.
    ///
    /// Measured through <c>TransformToAncestor</c> rather than through
    /// <c>RenderTransform.Value</c>. That property is the ScaleTransform's own matrix and does
    /// NOT carry the <c>RenderTransformOrigin</c> offset, so reading it would report a fill
    /// sweeping 0 to W/2 in both the correct and the mutated build; the composed
    /// visual-tree transform is the one that says where the ink actually lands. (Verified by
    /// mutation: at origin (0.5, 0) the composed span becomes 0.25W to 0.75W, and the
    /// <c>RenderTransform.Value</c> reading stays green.)
    /// </summary>
    [StaFact]
    public void Rail_GrowsFromTheLeftEdge()
    {
        var slider = Build(0, 1, 0.1, current: 0.5);
        LayOut(slider);

        // Recorded under Tokens.FontStandard at PlateWidth 180: of the plate's 152.00 DIP interior
        // the rail takes 88.26, the two 10 DIP gutters 20.00, and the Auto-width end-labels the
        // remaining 43.74 — 23.31 for "0.0" against 20.43 for "1.0", this face's digits not being
        // tabular. Read rather than restated below: what this test is about is where the fill sits
        // inside the rail, not how wide the rail came out.
        var width = slider.RailFill.ActualWidth;
        Check.WithCustomMessage(
                "the rail measured no width at all, so nothing below is measuring the layout")
            .That(width).IsStrictlyGreaterThan(0.0);

        var toTrack = slider.RailFill.TransformToAncestor(slider.RailTrack);
        var left = toTrack.Transform(new Point(0, 0)).X;
        var right = toTrack.Transform(new Point(width, 0)).X;

        Check.WithCustomMessage(
                $"the fill's painted left edge sits {left:F2} DIP in from the track's left edge — "
                + "it is growing from its centre, so it reads as 'the middle of the range' "
                + "rather than 'up to here'.")
            .That(left).IsCloseTo(0.0, 1e-9);
        Check.WithCustomMessage(
                $"at half the range the fill paints from {left:F2} to {right:F2} of an "
                + $"{width:F2} DIP rail; half of it is {width / 2:F2}.")
            .That(right).IsCloseTo(width / 2, 1e-9);

        // A diagnostic rather than the guarantee — the two checks above already measure where
        // the ink lands. This one names the property responsible, so the failure says "the
        // anchor moved" instead of leaving the reader to infer it from two offsets.
        Check.WithCustomMessage(
                $"RenderTransformOrigin is {slider.RailFill.RenderTransformOrigin}; its X must be "
                + "0 or the fill is anchored somewhere other than the minimum end of the rail.")
            .That(slider.RailFill.RenderTransformOrigin.X).IsEqualTo(0.0);
    }

    /// <summary>
    /// Every other test here uses [0, 1] — the friendliest range in the application. The plate
    /// is fixed-width and its end-labels are <c>Auto</c> columns either side of the rail's
    /// <c>Star</c>, so label width comes straight out of rail width: a range whose ends print
    /// wide leaves less rail, and does so silently, because nothing clips and nothing overflows.
    ///
    /// The widest real range is <c>qa_dismiss</c>, "Dismiss Timeout (ms)" over 1000 … 15000 at
    /// <c>decimals: 0</c> (src/EyeCtrl.App/Views/Settings/Sources/QuickActionsPageSource.cs:59)
    /// — nine digits of end-label against three for [0.0, 1.0], and the widest of all 26
    /// <c>SliderSpec</c> sites in the settings sources.
    ///
    /// The floor is 8x the rail's own 5 DIP height. Below roughly that aspect the rail stops
    /// reading as a bar whose fill can be judged at a glance and starts reading as a stub, which
    /// defeats the point of having it; it is a shape judgement, so it is stated as one rather
    /// than dressed up as a derived quantity.
    ///
    /// Measured: this range leaves the rail 50.44 DIP, against the 88.26 that [0.0, 1.0] leaves
    /// (see <see cref="Rail_GrowsFromTheLeftEdge"/>). So the widest range in the app already
    /// spends 37.82 DIP of rail on its labels and clears the floor by 10.44 — real headroom, but
    /// not much, which is the reason this is pinned rather than assumed.
    /// </summary>
    [StaFact]
    public void Rail_OnTheWidestRealRange_StaysWideEnoughToRead()
    {
        const double floorDip = 40;

        var slider = Build(1000, 15000, 500, current: 8000, decimals: 0);
        LayOut(slider);

        Check.That(slider.MinimumText.Text).IsEqualTo("1000");
        Check.That(slider.MaximumText.Text).IsEqualTo("15000");

        Check.WithCustomMessage(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Over the app's widest real range (qa_dismiss, 1000 to 15000) the rail is squeezed "
                + "to {0:F2} DIP by its end-labels, under the {1:F0} DIP floor. The labels are Auto "
                + "columns and the rail is the Star one, so widening the labels — a larger "
                + "FontSizeSubtitle, wider gutters, or a wider range reaching this control — takes "
                + "it straight off the rail. Widen DwellSlider's PlateWidth only as far as "
                + "SettingsContentFitTests.TheSliderRow_FitsTheContentColumn_OnANarrowDisplay "
                + "allows; otherwise shrink the gutters or the label font.",
                slider.RailTrack.ActualWidth, floorDip))
            .That(slider.RailTrack.ActualWidth).IsGreaterOrEqualThan(floorDip);
    }

    /// <summary>
    /// The plate is the only surface these three strings are ever read against, so its
    /// background is the one they have to clear. 18 DIP at normal weight is NOT WCAG "large
    /// text" (that needs >= 24 DIP, or >= 18.66 DIP bold), so the end labels are held to the
    /// full 4.5:1 rather than 3:1.
    ///
    /// This proves the control reaches for the right tokens under the ambient theme; it cannot
    /// see whether those tokens hold up in the other two.
    /// <see cref="Theme.SliderPlateContrastTests"/> is the other half of the pair — it gates the
    /// same three relationships at the token level across every registered theme.
    /// </summary>
    [StaFact]
    public void PlateText_IsLegibleAgainstThePlate()
    {
        var slider = Build(0, 1, 0.1, current: 0.5);

        var plate = Of(slider.Plate.Background);
        foreach (var (name, text) in new[]
                 {
                     ("value", slider.ValueText),
                     ("minimum label", slider.MinimumText),
                     ("maximum label", slider.MaximumText),
                 })
        {
            var ratio = Contrast(Of(text.Foreground), plate);
            Check.WithCustomMessage(
                    $"the plate's {name} reaches only {ratio:F2}:1 against the plate surface "
                    + $"({plate}); 18 DIP at normal weight is not WCAG large text, so it must "
                    + "clear 4.5:1.")
                .That(ratio).IsGreaterOrEqualThan(4.5);
        }

        var railRatio = Contrast(Of(slider.RailFill.Fill), Of(slider.RailTrack.Background));
        Check.WithCustomMessage(
                $"the rail's fill reaches only {railRatio:F2}:1 against its track — how far the "
                + "value has come is invisible, which is the whole point of the rail.")
            .That(railRatio).IsGreaterOrEqualThan(3.0);
    }

    /// <summary>
    /// The plate must fit the row it replaced a bare TextBlock in. <c>SettingsAutoPagination</c>
    /// measures row heights to decide where a page breaks, so a plate taller than the steppers
    /// either side would silently re-paginate every settings page that carries a slider.
    ///
    /// Compared against the stepper buttons themselves, not against a literal 100.
    /// <c>DwellSlider</c> never passes <c>height</c> to <see cref="DwellButton"/>, so the row's
    /// height is that constructor's default — and a change to the default would leave a literal
    /// assertion green while the plate stopped matching its own row.
    ///
    /// Two halves, because the declared height alone is not the guarantee. The plate's own
    /// <c>DesiredSize</c> is useless here — an explicit <c>Height</c> makes it exactly that
    /// whatever the content wants, and WPF then clips the overflow rather than reporting it. So
    /// the second half re-measures the plate's content unconstrained vertically, at the plate's
    /// own inner width, which is the only reading that can say "this no longer fits".
    /// </summary>
    [StaFact]
    public void ThePlate_IsExactlyAsTallAsItsStepperButtons()
    {
        var slider = Build(0, 1, 0.1, current: 0.5);
        LayOut(slider);

        var steppers = slider.Children.ToList();
        Check.WithCustomMessage(
                "the slider no longer exposes exactly the two stepper buttons this compares the "
                + "plate against")
            .That(steppers.Count).IsEqualTo(2);

        foreach (var stepper in steppers)
        {
            Check.WithCustomMessage(
                    $"the plate is {slider.Plate.Height} DIP tall but the stepper button beside "
                    + $"it is {stepper.Root.Height} — the row is now as tall as whichever is "
                    + "larger, and every SettingsAutoPagination row count shifts with it.")
                .That(slider.Plate.Height).IsEqualTo(stepper.Root.Height);
        }

        var content = (FrameworkElement)slider.Plate.Child;
        var inner = slider.Plate.Width - slider.Plate.Padding.Left - slider.Plate.Padding.Right;

        // Measure() short-circuits on IsMeasureValid, and this content was just measured against
        // the plate's fixed height. Invalidate it or the call below hands back that clamped
        // reading and the assertion is vacuous.
        content.InvalidateMeasure();
        content.Measure(new Size(inner, double.PositiveInfinity));

        Check.WithCustomMessage(
                $"the plate's content wants {content.DesiredSize.Height:F2} DIP at its {inner:F2} "
                + $"DIP inner width, more than the {slider.Plate.Height} DIP plate it is drawn in "
                + "— WPF will clip it rather than grow the row, so the value or an end-label is "
                + "now cut off.")
            .That(content.DesiredSize.Height).IsLessOrEqualThan(slider.Plate.Height);
    }

    // ────── Whose label is it: the composite's, or the host's? ──────

    /// <summary>
    /// A slider that draws no heading of its own — the form a two-column
    /// settings row hosts.
    /// </summary>
    private static DwellSlider BuildHeadless() =>
        new(new FakeDwellRegistrar(), "sl", "Smoothing", 0, 10, 1, 1,
            () => 5, _ => { }, DwellCategory.Control, 50, 800, showLabel: false);

    /// <summary>
    /// A two-column settings row lays the setting's NAME out itself, in its left column, so the
    /// composite it hosts must attach its controls and nothing else. A heading of its own would put
    /// the same name on screen twice.
    ///
    /// <para>
    /// Counted over the HOST panel's own children: the heading is the only <c>TextBlock</c> this
    /// composite ever parents to its host (the value and the range's end-labels live inside the plate), so an empty top-level set is
    /// exactly "it attached no heading of its own".
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

        Build(0, 10, 1, 5).Attach(panel);

        Check.WithCustomMessage(
                "the default form must still draw its own heading — SettingsWindow and every "
                + "other caller passes no flag and expects the row it always got.")
            .That(panel.Children.OfType<TextBlock>().Single().Text).IsEqualTo("Smoothing");
    }

}
