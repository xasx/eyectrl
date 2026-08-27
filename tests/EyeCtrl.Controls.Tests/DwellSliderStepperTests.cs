namespace EyeCtrl.Controls.Tests;

using System;
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
/// <see cref="DwellSlider"/>'s two steppers are the only things on a slider row a gaze user can
/// aim at, and they have to LOOK like it.
///
/// <para>
/// They did not. Each is drawn 100 x 100 — the WYSIWYG floor, exactly the hit circle it registers
/// — and live testing on the tracker still reported them as "too small / hard to aim at". The
/// defect was not size: it was that <c>BtnBase</c> is <see cref="Tokens.SurfaceMedium"/> on a
/// settings page of <see cref="Tokens.SurfaceDark"/>, a 1.23:1 fill step sampled off a real
/// render as <c>#292944</c> on <c>#19192D</c>. The 100 DIP extent was invisible, so the user
/// aimed at the glyph. Every other control on that page that IS comfortable to aim at carries a
/// real edge: combo options have a border, swatches are colour-filled, the Reset button is
/// <c>AccentWarning</c>, and <c>DwellToggle</c>'s switch has its outline. The steppers were the
/// only ones with a fill and no border.
/// </para>
/// <para>
/// This is the STRUCTURAL half of a pair. It reads the real brushes off a constructed control
/// under one ambient theme, so it proves this composite draws an outline at all — which the
/// palette sweep cannot see, and which a tidy-up deleting two lines from the constructor would
/// otherwise take away silently. <c>Theme.SliderStepperContrastTests</c> is the other half: it
/// reads the palette in every registered theme, and stays green on any code change. The same
/// division of labour as <c>DwellToggleVisualTests</c> against <c>Theme.ToggleContrastTests</c>.
/// </para>
/// </summary>
public class DwellSliderStepperTests
{
    /// <summary>WCAG 1.4.11: a non-text component boundary owes 3:1.</summary>
    private const double ComponentBoundaryFloor = 3.0;

    /// <summary>
    /// The outline weight, restated here rather than read off <see cref="DwellSlider"/> — a guard
    /// that asks the subject what it should be guards nothing. Two, matching
    /// <c>DwellToggle.SwitchOutline</c>: the aimable controls of a settings page carry one edge
    /// weight, and a dwell target's perceptibility must not ride on a theme's card-outline token
    /// (<see cref="Tokens.BorderThicknessStandard"/> is 1 in Midnight and Ember).
    /// </summary>
    private const double ExpectedOutline = 2;


    private static DwellSlider Build() =>
        new(new FakeDwellRegistrar(), "sl", "Smoothing", 0, 1, 0.1, 1,
            () => 0.5, _ => { }, DwellCategory.Control, 50, 800);

    /// <summary>
    /// Attaches and runs a real layout pass in a host large enough not to constrain the row.
    /// A detached tree short-circuits <c>Measure</c> on <c>IsMeasureValid</c>, so everything that
    /// reads an arranged size goes through here once and only once.
    /// </summary>
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

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Both steppers, named, so a failure says which one lost its edge.</summary>
    public static TheoryData<int, string> BothSteppers() => new() { { 0, "minus" }, { 1, "plus" } };

    /// <summary>
    /// The stepper draws an outline, and it draws it in the brush the palette sweep measures.
    ///
    /// <para>
    /// The KEY-LINK, and it is the load-bearing half of the pair.
    /// <c>Theme.SliderStepperContrastTests.TheStepper_IsPerceptibleAgainstThePage</c> measures
    /// <c>theme.TextSecondary</c> against <c>theme.SurfaceDark</c> — a hardcoded pairing. Repoint
    /// this border at anything else and that gate keeps measuring TextSecondary and keeps passing
    /// while the control goes invisible; point it at the stepper's own
    /// <see cref="Tokens.BrushSurfaceMedium"/> — the 1.23:1 step this whole change exists to
    /// escape — and it would stay green over exactly the defect that was reported. So the token is
    /// pinned here by VALUE.
    /// </para>
    /// <para>
    /// Colour, not alpha, and not brush identity. An opaque fill reads alpha 255 whether or not a
    /// boundary was drawn, so alpha proves nothing; and reference identity would break on any
    /// theme that hands out fresh brushes rather than frozen singletons.
    /// </para>
    /// </summary>
    /// <param name="index">Which of <c>Children</c> to inspect.</param>
    /// <param name="name">That child's name, for the failure message.</param>
    [StaTheory]
    [MemberData(nameof(BothSteppers))]
    public void TheStepper_NamesABrushAndAThickness(int index, string name)
    {
        var root = Build().Children[index].Root;

        Check.WithCustomMessage(
                $"the {name} stepper's BorderThickness is {root.BorderThickness.Top}, so it draws "
                + "no outline at all and the 100 DIP target is back to a 1.23:1 fill step against "
                + "the settings page — invisible, which is the defect live testing reported as "
                + "\"too small / hard to aim at\". A brush and a thickness are both required; "
                + "neither implies the other.")
            .That(root.BorderThickness.Top).IsEqualTo(ExpectedOutline);

        Check.WithCustomMessage(
                $"the {name} stepper's BorderThickness is not uniform "
                + $"({root.BorderThickness}). Three sides of an edge is not an edge.")
            .That(root.BorderThickness)
            .IsEqualTo(new Thickness(ExpectedOutline));

        Check.WithCustomMessage(
                $"the {name} stepper declares no BorderBrush, so its BorderThickness draws "
                + "nothing. Both are required.")
            .That(root.BorderBrush).IsNotNull();

        var drawn = ((SolidColorBrush)root.BorderBrush).Color;

        Check.WithCustomMessage(
                $"the {name} stepper's BorderBrush is {drawn}, not TextSecondary "
                + $"({Tokens.TextSecondary}). Theme.SliderStepperContrastTests measures "
                + "TextSecondary against SurfaceDark, so pointing this border at any other token "
                + "leaves that sweep measuring a pairing the control does not use — it would stay "
                + "green over an invisible target. Repointed at the stepper's own SurfaceMedium "
                + "it would be a literal 1.00:1 and nothing else in the suite would notice. "
                + "Repoint the sweep too if this is deliberate.")
            .That(drawn).IsEqualTo(Tokens.TextSecondary);
    }

    /// <summary>
    /// The edge is actually perceptible against the surface behind it. Same
    /// <c>max(fill, outline)</c> shape as the switch, the track, the knob and the dwell bar — the
    /// stepper may earn its boundary either way, and today only the outline does.
    ///
    /// <para>
    /// The backdrop is <see cref="Tokens.SurfaceDark"/> because the settings content card carries
    /// no fill of its own; a filled card is what collapsed this to 1.00:1 and is gated separately
    /// by <c>SettingsContentCardBoundaryTests.TheCard_CarriesNoFillOfItsOwn</c>.
    /// </para>
    /// </summary>
    /// <param name="index">Which of <c>Children</c> to inspect.</param>
    /// <param name="name">That child's name, for the failure message.</param>
    [StaTheory]
    [MemberData(nameof(BothSteppers))]
    public void TheStepper_HasAPerceptibleBoundaryAgainstThePage(int index, string name)
    {
        var root = Build().Children[index].Root;

        var page = Tokens.SurfaceDark;
        var fill = Contrast(((SolidColorBrush)root.Background).Color, page);
        var outline = root.BorderThickness.Top > 0 && root.BorderBrush is SolidColorBrush b
            ? Contrast(b.Color, page)
            : 1.0; // no outline drawn at all -> the fill has to carry it, and it cannot
        var best = Math.Max(fill, outline);

        Check.WithCustomMessage(
                $"the {name} stepper reaches only {best:F2}:1 against the settings page (fill "
                + $"{fill:F2}:1, outline {outline:F2}:1 at thickness {root.BorderThickness.Top}). "
                + "Below 3:1 the 100 DIP dwell target has no visible extent and the user is "
                + "aiming at a bare glyph.")
            .That(best).IsGreaterOrEqualThan(ComponentBoundaryFloor);
    }

    /// <summary>
    /// The outline costs the stepper no SIZE — the trap a border on a WPF <see cref="Border"/>
    /// always sets.
    ///
    /// <para>
    /// <c>BorderThickness</c> deflates the CHILD, so on a content-sized root an outline would grow
    /// the box by twice the thickness and break the WYSIWYG floor in the other direction: a
    /// control drawn LARGER than the hit circle it registers is just as much a lie as one drawn
    /// smaller. <see cref="DwellButton"/> gives <c>Root</c> an explicit <c>Width</c> and
    /// <c>Height</c> (DwellButton.cs:165-166), which is what makes this safe — but "is what makes
    /// this safe" is a claim about code someone else owns, so it is measured here rather than
    /// trusted. Recorded before and after the outline landed: 100.00 x 100.00 either way.
    /// </para>
    /// <para>
    /// The row's natural width is pinned in the same pass, because
    /// <c>SettingsContentFitTests.TheSliderRow_FitsTheContentColumn_OnANarrowDisplay</c> holds it
    /// at 404 DIP against a 416 DIP column — 12 DIP of headroom, which two more steppers' worth of
    /// border would have eaten a third of had the roots been content-sized. Measured: 404.00
    /// before and after.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheOutline_CostsTheStepperNoSize()
    {
        var slider = Build();
        LayOut(slider);

        foreach (var (index, name) in new[] { (0, "minus"), (1, "plus") })
        {
            var root = slider.Children[index].Root;

            Check.WithCustomMessage(
                    $"the {name} stepper is arranged {root.ActualWidth:F2} x "
                    + $"{root.ActualHeight:F2}, not 100 x 100. A Border deflates its CHILD, so an "
                    + "outline on a content-sized root grows the outer box by twice its "
                    + "thickness. The WYSIWYG floor says a settings dwell control is never drawn "
                    + "smaller than the 100 DIP hit circle it registers — and it must not be "
                    + "drawn larger by accident either, or what the user aims at stops being what "
                    + "they see.")
                .That((root.ActualWidth, root.ActualHeight)).IsEqualTo((100.0, 100.0));
        }
    }

    /// <summary>
    /// The slider row's natural width did not move. Read off a live measure rather than restated:
    /// what this pins is that the outline changed it by nothing, not what the number happens to
    /// be.
    /// </summary>
    [StaFact]
    public void TheOutline_DoesNotWidenTheRow()
    {
        var slider = Build();
        var panel = new StackPanel();
        TextElement.SetFontFamily(panel, Tokens.FontStandard);
        slider.Attach(panel);
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // 108 + 188 + 108: two steppers at their 100 DIP box plus DwellButton's (4,0,4,0) margin,
        // either side of the 180 DIP plate plus its own (4,0,4,0). The outline lives inside the
        // 100 and inside the margin, so none of the three terms moved.
        var row = panel.Children[1].DesiredSize.Width;

        Check.WithCustomMessage(
                $"the slider's stepper row now wants {row:F2} DIP, not 404.00. "
                + "SettingsContentFitTests.TheSliderRow_FitsTheContentColumn_OnANarrowDisplay "
                + "holds this row against a 416 DIP content column on the narrowest supported "
                + "display, and the row is a composite over a horizontal StackPanel — nothing "
                + "caps it and it CLIPS rather than wraps, so the (+) button would simply be "
                + "drawn off the panel. If the steppers' outline has started costing layout, the "
                + "roots have lost their explicit size; see TheOutline_CostsTheStepperNoSize.")
            .That(row).IsEqualTo(404.0);
    }

    /// <summary>
    /// The value plate gets NO outline, and that is a decision rather than an omission.
    ///
    /// <para>
    /// The plate carries the same <see cref="Tokens.SurfaceMedium"/> fill and therefore the same
    /// 1.23:1 — but it registers no dwell target, so this is legibility, not aiming, and its
    /// legibility is already carried by its contents and gated as such
    /// (<c>Theme.SliderPlateContrastTests</c> holds the value and the end labels at 4.5:1 and the
    /// rail fill at 3:1). Rendered all three ways and looked at: with the plate outlined to match
    /// the steppers the row reads as three identical boxes, and the two things the user can
    /// actually aim at stop being the prominent ones; with a subordinate BorderSubtle edge it is
    /// quieter but still competes. Unoutlined, the two steppers frame a passive readout, which is
    /// what the row is.
    /// </para>
    /// <para>
    /// There is a layout cost too, and it points the same way: a Border deflates its child, so an
    /// outline here would take 2 DIP per axis out of the plate's interior (4 in Contrast) — off
    /// the rail, whose width
    /// <c>DwellSliderPlateTests.Rail_OnTheWidestRealRange_StaysWideEnoughToRead</c> clears its
    /// floor by only 10.44 DIP.
    /// </para>
    /// <para>
    /// Not a prohibition forever: if the plate should get an edge, render the row first and change
    /// this test deliberately.
    /// </para>
    /// </summary>
    [StaFact]
    public void ThePlate_CarriesNoOutlineOfItsOwn()
    {
        var slider = Build();
        LayOut(slider);

        Check.WithCustomMessage(
                $"the value plate now draws a {slider.Plate.BorderThickness.Top} DIP outline. It "
                + "registers no dwell target, so an edge buys it nothing an aimable control needs "
                + "— and rendered, an outlined plate flattens the row into three identical boxes "
                + "and de-emphasises the two steppers, which are the only things on this row a "
                + "gaze user can hit. It also deflates the plate's interior, which comes straight "
                + "off the rail. If this is wanted, re-render the row and re-decide.")
            .That(slider.Plate.BorderThickness).IsEqualTo(default(Thickness));
    }
}
