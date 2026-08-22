namespace EyeCtrl.Controls.Tests;

using System;
using System.Collections.Generic;
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
/// The value plate must say what the bare number never did: where this value sits between its
/// minimum and maximum. A gaze user dwelling "+" otherwise has no way to know they are at an end.
/// </summary>
public class DwellSliderPlateTests
{
    /// <summary>A slider over [min, max] reporting a fixed current value. Decimals: 1.</summary>
    private static DwellSlider Build(double min, double max, double step, double current) =>
        new(new FakeRegistrar(), "sl", "Smoothing", min, max, step, 1,
            () => current, _ => { }, DwellCategory.Control, 50, 800);

    /// <summary>
    /// Attaches <paramref name="slider"/> to a scratch panel inside a generously sized host and
    /// runs a real layout pass, so <c>ActualWidth</c> and <c>DesiredSize</c> mean something. The
    /// theme font is pinned because <c>SettingsWindow</c> is an <c>OverlayWindow</c>, which sets
    /// it and lets it inherit down — measuring in WPF's default face would report widths the
    /// application never produces.
    /// </summary>
    private static StackPanel LayOut(DwellSlider slider)
    {
        var panel = new StackPanel();
        TextElement.SetFontFamily(panel, Tokens.FontStandard);
        slider.Attach(panel);

        var host = new Border { Child = panel, Width = 600, Height = 400 };
        host.Measure(new Size(600, 400));
        host.Arrange(new Rect(0, 0, 600, 400));
        host.UpdateLayout();
        return panel;
    }

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

        Check.That(slider.RailFraction).IsEqualTo(0.5);
    }

    [StaFact]
    public void Rail_AtTheMinimum_IsEmpty_AndAtTheMaximum_IsFull()
    {
        var slider = new DwellSlider(
            new FakeRegistrar(), "sl", "Thickness", 1, 6, 0.5, 1,
            () => 1.0, v => { }, DwellCategory.Control, 50, 800);
        Check.That(slider.RailFraction).IsEqualTo(0.0);

        var atMax = new DwellSlider(
            new FakeRegistrar(), "sl", "Thickness", 1, 6, 0.5, 1,
            () => 6.0, v => { }, DwellCategory.Control, 50, 800);
        Check.That(atMax.RailFraction).IsEqualTo(1.0);
    }

    [StaFact]
    public void Rail_OnADegenerateRange_DoesNotDivideByZero()
    {
        // min == max is nonsense but must not produce NaN and paint an invisible rail.
        var slider = new DwellSlider(
            new FakeRegistrar(), "sl", "Fixed", 3, 3, 1, 0,
            () => 3.0, v => { }, DwellCategory.Control, 50, 800);

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
        var registrar = new FakeRegistrar();
        var value = 0.5;
        var slider = new DwellSlider(
            registrar, "sl", "Smoothing", 0, 1, 0.1, 1,
            () => value, v => value = v, DwellCategory.Control, 50, 800);

        // Registration happens in Attach, not in the constructor, so the activation the
        // registrar fires below only exists once the composite is on a panel.
        LayOut(slider);

        Check.That(slider.ValueText.Text).IsEqualTo("0.5");
        Check.That(slider.RailFraction).IsEqualTo(0.5);

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
            .That(slider.RailFraction).IsEqualTo(0.6);
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

        // Recorded under Tokens.FontStandard at PlateWidth 180: a 152 DIP plate interior less
        // the two 18 DIP end-labels and their 10 DIP gutters leaves the rail 88.26 DIP. Read
        // rather than restated below — the point of this test is where the fill sits inside the
        // rail, not how wide the rail came out.
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
            .That(left).IsEqualTo(0.0);
        Check.WithCustomMessage(
                $"at half the range the fill paints from {left:F2} to {right:F2} of an "
                + $"{width:F2} DIP rail; half of it is {width / 2:F2}.")
            .That(right).IsEqualTo(width / 2);
        Check.WithCustomMessage(
                $"RenderTransformOrigin is {slider.RailFill.RenderTransformOrigin}; its X must be "
                + "0 or the fill is anchored somewhere other than the minimum end of the rail.")
            .That(slider.RailFill.RenderTransformOrigin.X).IsEqualTo(0.0);
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
    /// measures row heights to decide where a page breaks, and the stepper buttons either side
    /// are 100 DIP tall, so a plate that measures taller silently re-paginates every settings
    /// page that carries a slider.
    ///
    /// Two halves, because the declared height alone is not the guarantee. The plate's own
    /// <c>DesiredSize</c> is useless here — an explicit <c>Height</c> makes it 100 whatever the
    /// content wants, and WPF then clips the overflow rather than reporting it. So the second
    /// half re-measures the plate's content unconstrained vertically, at the plate's own inner
    /// width, which is the only reading that can say "this no longer fits".
    /// </summary>
    [StaFact]
    public void ThePlate_IsNoTallerThanTheRowItSitsIn()
    {
        var slider = Build(0, 1, 0.1, current: 0.5);
        LayOut(slider);

        Check.WithCustomMessage(
                $"the plate is {slider.Plate.Height} DIP tall against the 100 DIP stepper "
                + "buttons it sits between.")
            .That(slider.Plate.Height).IsEqualTo(100.0);

        var content = (FrameworkElement)slider.Plate.Child;
        var inner = slider.Plate.Width - slider.Plate.Padding.Left - slider.Plate.Padding.Right;

        // Measure() short-circuits on IsMeasureValid, and this content was just measured against
        // the plate's fixed 100 DIP. Invalidate it or the call below hands back that clamped
        // reading and the assertion is vacuous.
        content.InvalidateMeasure();
        content.Measure(new Size(inner, double.PositiveInfinity));

        Check.WithCustomMessage(
                $"the plate's content wants {content.DesiredSize.Height:F2} DIP at its {inner:F2} "
                + "DIP inner width, more than the 100 DIP plate it is drawn in — WPF will clip it "
                + "rather than grow the row, so the value or an end-label is now cut off.")
            .That(content.DesiredSize.Height).IsLessOrEqualThan(100.0);
    }

    /// <summary>
    /// Captures each registered activation by id so a test can fire one the way the overlay's
    /// dwell detector would.
    /// </summary>
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
