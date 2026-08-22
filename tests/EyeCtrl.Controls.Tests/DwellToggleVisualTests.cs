namespace EyeCtrl.Controls.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// <see cref="DwellToggle"/> is a COMPOSITE: a plain label the user reads, plus a switch that is
/// the only dwell target. Both halves of that sentence are accessibility, not styling.
///
/// <para>
/// Why the target moved onto the switch. <see cref="DwellControl.UpdatePosition"/> registers the
/// hit circle at <see cref="DwellControl.Root"/>'s CENTRE, so a one-<c>DwellControl</c> row
/// carrying a label on the left and a switch on the right would put a 100 DIP circle in the
/// middle of a ~416 DIP row: the user would have to dwell the gap between the two, and looking at
/// the switch — the obvious thing to look at — would miss by roughly 130 DIP. The registration
/// tests below (<see cref="TheLabelIsNotADwellTarget"/>,
/// <see cref="ActivationFlowsThroughTheRegistrar"/>) are what hold that.
/// </para>
/// <para>
/// Why the label moved off the state fill. #D6DEF5 on the #4A8F5C ON fill measures 2.91:1 — under
/// the 3:1 large-text bar, at rest, before any dwell — and no amount of holding the fill still
/// fixes a resting number. On the row surface the same label measures 10.33:1
/// (<see cref="TheLabelSitsOnTheRowSurface_AndClearsFourAndAHalfToOneThere"/>).
/// </para>
/// <para>
/// Dwell feedback is the switch's own <see cref="DwellButton.ProgressBar"/>, exercised through a
/// fake registrar and the real <see cref="DwellControl.UpdateVisual"/> rather than a test-only
/// hook on the production type — the convention <c>DwellColorPickerProgressTests</c> and
/// <c>DwellProgressContrastTests</c> established. One test renders instead of reading DPs:
/// <see cref="TheDwellBarPaintsThroughTheWholeCard"/>, because a bar that occupies the visual
/// tree and paints nothing satisfies every DP in this file.
/// </para>
/// </summary>
public class DwellToggleVisualTests
{
    /// <summary>The dwell radius every toggle here is built with, in the same units as DIP at 96 dpi.</summary>
    private const double Radius = 50;

    /// <summary>
    /// A toggle over a CAPTURED bool, so <c>Toggle()</c> really flips the value the control reads
    /// back. A throwaway setter (<c>_ => { }</c>) would leave every assertion below checking a
    /// state that never changed.
    /// </summary>
    private static DwellToggle Build(bool initial) => Build(new FakeRegistrar(), initial);

    /// <summary>
    /// As <see cref="Build(bool)"/>, but activations land in <paramref name="registrar"/> so a
    /// test can fire one the way the overlay's dwell detector would.
    /// </summary>
    private static DwellToggle Build(FakeRegistrar registrar, bool initial)
    {
        var state = initial;
        return new DwellToggle(
            registrar, "id", "Label", () => state, v => state = v,
            DwellCategory.Control, Radius, 800);
    }

    /// <summary>
    /// Attaches <paramref name="toggle"/> to a scratch panel and lays it out at the narrowest
    /// content column the settings window supports, under the theme font — the same conditions
    /// <c>SettingsAutoPagination</c> measures in. Returns the panel.
    /// </summary>
    private static StackPanel AttachAndLayout(DwellToggle toggle, double width = 416)
    {
        var panel = new StackPanel { Width = width };
        TextElement.SetFontFamily(panel, Tokens.FontStandard);
        toggle.Attach(panel);
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        panel.UpdateLayout();
        return panel;
    }

    // ────── Anatomy ──────

    [StaFact]
    public void TheLabelIsPlainText_AndTheSwitchIsTheOnlyTarget()
    {
        var toggle = Build(initial: false);

        Check.That(toggle.Label.Text).IsEqualTo("Label");
        Check.That(toggle.Children.Count()).IsEqualTo(1);
        Check.WithCustomMessage("Children must expose the switch itself, not a copy of it.")
            .That(ReferenceEquals(toggle.Children.Single(), toggle.Switch)).IsTrue();
    }

    /// <summary>
    /// State must survive a colour-vision deficiency and a monochrome screen alike, so the word
    /// is the primary carrier and the knob's side and the track's fill back it up.
    /// </summary>
    [StaFact]
    public void StateIsReadableWithoutColour()
    {
        var toggle = Build(initial: false);

        Check.That(toggle.StateText.Text).IsEqualTo("Off");
        toggle.Toggle();
        Check.That(toggle.StateText.Text).IsEqualTo("On");
    }

    [StaFact]
    public void TheKnobMovesSide_SoPositionCarriesStateToo()
    {
        var toggle = Build(initial: false);

        Check.That(toggle.Knob.HorizontalAlignment).IsEqualTo(HorizontalAlignment.Left);
        toggle.Toggle();
        Check.That(toggle.Knob.HorizontalAlignment).IsEqualTo(HorizontalAlignment.Right);
    }

    [StaFact]
    public void TheTrackCarriesTheStateColour()
    {
        var toggle = Build(initial: true);

        Check.That(((SolidColorBrush)toggle.Track.Background).Color).IsEqualTo(Tokens.ToggleOn);
        toggle.Toggle();
        Check.That(((SolidColorBrush)toggle.Track.Background).Color).IsEqualTo(Tokens.ToggleOff);
    }

    /// <summary>
    /// WYSIWYG dwell floor, restated for the switch because the switch — not the row — is what
    /// registers now. <c>DwellButton</c>'s own doc records the rule: a settings dwell control is
    /// never drawn smaller than the 100 DIP hit circle it registers.
    ///
    /// <para>
    /// Measured on the ARRANGED box, not on <c>Root.Width</c>/<c>Root.Height</c>. Those are the
    /// constructor's arguments and never change whatever the parent does — which is precisely the
    /// failure this control's own card border introduced: measured, a bordered card handed the
    /// switch a layout clip of 0;0;160;98 at <c>BorderThicknessStandard</c> 1 and 0;0;160;96 at 2,
    /// and a DP-reading version of this test stayed green through both. So it reads
    /// <c>ActualWidth</c>/<c>ActualHeight</c> after a real layout pass AND intersects them with the
    /// layout clip, because either alone can be the thing that shrinks.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheSwitchIsNeverDrawnSmallerThanItsHitCircle()
    {
        var toggle = Build(initial: true);
        AttachAndLayout(toggle);
        var root = toggle.Switch.Root;

        var clip = LayoutInformation.GetLayoutClip(root);
        var drawnWidth = clip is null
            ? root.ActualWidth
            : Math.Min(root.ActualWidth, clip.Bounds.Width);
        var drawnHeight = clip is null
            ? root.ActualHeight
            : Math.Min(root.ActualHeight, clip.Bounds.Height);

        Check.WithCustomMessage(
                $"the switch is DRAWN {drawnWidth}x{drawnHeight} DIP (declared "
                + $"{root.Width}x{root.Height}, layout clip {clip?.Bounds.ToString() ?? "none"}), "
                + "inside the 100 DIP circle it registers — the user would be activating it from "
                + "points where nothing is drawn.")
            .That(Math.Min(drawnWidth, drawnHeight)).IsGreaterOrEqualThan(100.0);
    }

    /// <summary>
    /// The plan this task implements sized the switch at 140 DIP, which is 12 DIP NARROWER than
    /// the content it holds: the state word's box plus its margins is 68 and the track is 84.
    /// <c>DwellButton</c> installs a rounded clip over its layout, so the overflow would have been
    /// silently cut rather than drawn — and every other test in this file reads a DP or a brush
    /// and would have stayed green. Measured here instead of restated: the content is measured
    /// unconstrained and compared against the drawn width.
    /// </summary>
    [StaFact]
    public void TheSwitchIsWideEnoughForItsOwnContent()
    {
        var toggle = Build(initial: false);
        var content = (FrameworkElement)toggle.Switch.Content!;
        var root = toggle.Switch.Root;

        AttachAndLayout(toggle);
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // The content's real budget is the box less the switch's own outline, which a Border
        // deflates its child by. Reading the thickness off the element rather than restating it,
        // so thickening the outline is caught here instead of quietly eating the track's edge.
        var budget = root.Width - root.BorderThickness.Left - root.BorderThickness.Right;

        Check.WithCustomMessage(
                $"the switch's content wants {content.DesiredSize.Width:F2} DIP against a "
                + $"{budget:F2} DIP budget ({root.Width} wide less a "
                + $"{root.BorderThickness.Left}/{root.BorderThickness.Right} outline). DwellButton "
                + "clips its layout to the rounded silhouette, so the overflow is cut, not drawn — "
                + "the track or the state word loses its edge with nothing thrown.")
            .That(content.DesiredSize.Width).IsLessOrEqualThan(budget);
    }

    [StaFact]
    public void DwellSweepsTheSwitchesProgressBar()
    {
        var registrar = new FakeRegistrar { Progress = 0.75 };
        var toggle = Build(registrar, initial: true);

        toggle.Switch.UpdateVisual();

        Check.That(((ScaleTransform)toggle.Switch.ProgressBar.RenderTransform).ScaleX)
            .IsEqualTo(0.75);

        // The geometry claim that let Theme.ToggleContrastTests retire its
        // ProgressBar_IsPerceptibleAgainstEitherToggleFill theory: the bar and the track occupy
        // DIFFERENT rows of the switch's layout, so the bar is never drawn on a state fill and
        // that pairing cannot occur. Asserted rather than assumed.
        Check.WithCustomMessage(
                "the dwell bar and the track are no longer in separate layout rows, so the bar can "
                + "be drawn over the state fill again — restore the cross-theme bar-vs-ToggleOn/Off "
                + "gate that was retired on the strength of this separation.")
            .That(Grid.GetRow(toggle.Switch.ProgressBar)).IsEqualTo(1);
        Check.That(Grid.GetRow((UIElement)toggle.Switch.Content!)).IsEqualTo(0);
    }

    /// <summary>
    /// The switch has to be inset from the card's right edge, or its rounded corners sit on top of
    /// the card's and the row reads as one malformed shape. Measured before the inset existed: on a
    /// 360-wide card the switch was arranged at x = 200…360, flush against the edge, while the
    /// label got a 20 DIP inset on the other side.
    ///
    /// <para>
    /// Horizontal only, deliberately. Vertically the switch DOES span the card exactly, and must:
    /// it may not be drawn under 100 DIP (the WYSIWYG floor
    /// <see cref="TheSwitchIsNeverDrawnSmallerThanItsHitCircle"/> holds) and the card may not grow
    /// past 100 without moving the 124 DIP row height every settings page is paginated on. So this
    /// asserts the gap that is available and states why the other one is not.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheSwitchIsInsetFromTheCardsRightEdge()
    {
        var toggle = Build(initial: true);
        var panel = AttachAndLayout(toggle);
        var card = toggle.RowSurface;
        var root = toggle.Switch.Root;

        var switchRight = root.TransformToAncestor(panel).Transform(new Point(0, 0)).X
                          + root.ActualWidth;
        var cardRight = card.TransformToAncestor(panel).Transform(new Point(0, 0)).X
                        + card.ActualWidth - card.BorderThickness.Right;

        Check.WithCustomMessage(
                $"the switch's right edge is at {switchRight:F2} DIP and the card's inner edge at "
                + $"{cardRight:F2} — the switch is flush against (or past) the card, so their "
                + "rounded corners overlap. The card carries no Padding, so the inset has to come "
                + "from the switch's own Margin.")
            .That(switchRight).IsStrictlyLessThan(cardRight);
    }

    // ────── T1: exactly one target, and it is the switch ──────

    /// <summary>
    /// The defect the whole restructure exists to prevent, and the one thing none of the anatomy
    /// tests above can see: a stray SECOND registration — the label wired up as a target too, or
    /// the row registered alongside its switch — leaves every one of them green.
    ///
    /// Two directions are asserted, because either alone leaves a real hole. Every registration
    /// must be a <see cref="IDwellComposite.Children"/> member, or the per-frame loop (which
    /// iterates Children, and nothing else) never calls <see cref="DwellControl.UpdatePosition"/>
    /// for it and the target sits at the origin forever — the (0,0) orphan-target bug. And every
    /// Child must be registered, or it is drawn as a switch that cannot be activated.
    /// </summary>
    [StaFact]
    public void TheLabelIsNotADwellTarget()
    {
        var registrar = new FakeRegistrar();
        var toggle = Build(registrar, initial: false);
        var panel = new StackPanel();
        toggle.Attach(panel);

        Check.WithCustomMessage(
                "the toggle registered " + registrar.Registered.Count + " dwell targets ("
                + string.Join(", ", registrar.Registered) + "). Exactly one is correct: the "
                + "switch. A second target on the label — or on the row — puts a 100 DIP hit "
                + "circle under something the user is only meant to read.")
            .That(registrar.Registered).ContainsExactly(toggle.Switch.DwellId);

        Check.WithCustomMessage(
                "the registered ids and the composite's Children have diverged. The overlay's "
                + "per-frame loop iterates Children ONLY, so a registered target missing from it "
                + "never gets a position update and stays at the origin; a Child that was never "
                + "registered is drawn but cannot be activated.")
            .That(toggle.Children.Select(c => c.DwellId).ToArray())
            .ContainsExactly(registrar.Registered.ToArray());
    }

    // ────── T2: the track is perceptible against the fill it actually sits on ──────

    /// <summary>
    /// The track sits INSIDE the switch, whose fill is <see cref="Tokens.SurfaceLight"/> — not on
    /// the row surface. Measured against SurfaceLight, the state fills alone are
    /// ToggleOn 2.76 / 2.70 / 11.03 and ToggleOff 1.23 / 1.25 / 1.39 across
    /// Midnight / Ember / Contrast: the ON track fails the 3:1 non-text-component bar in two of
    /// three themes and the OFF track is effectively INVISIBLE in all three — the switch would
    /// have no visible extent at all when off. So the track carries a
    /// <see cref="Tokens.BrushTextSecondary"/> outline, which measures 4.38 / 5.38 / 12.35 against
    /// SurfaceLight and makes the track's perceptibility independent of its fill.
    ///
    /// Gates the same combined quantity the rest of this repo does for a fill-plus-outline
    /// element — max(fill, outline) vs the surface behind it — reading the REAL brushes off a
    /// constructed control. The cross-theme half is
    /// <c>Theme.ToggleContrastTests.TheTrack_IsPerceptibleAgainstTheSwitchFill</c>.
    /// </summary>
    [StaFact]
    public void TheTrackIsPerceptibleAgainstTheSwitchFill()
    {
        foreach (var initial in new[] { true, false })
        {
            var toggle = Build(initial);

            var switchFill = ((SolidColorBrush)toggle.Switch.Root.Background).Color;
            var trackFill = ((SolidColorBrush)toggle.Track.Background).Color;
            var outline = toggle.Track.BorderBrush is SolidColorBrush b
                ? Contrast(b.Color, switchFill)
                : 1.0; // no outline at all -> no rescue, which is the defect exactly
            var fill = Contrast(trackFill, switchFill);
            var best = Math.Max(fill, outline);

            Check.WithCustomMessage(
                    $"state={initial}: the track reaches only {best:F2}:1 against the switch's own "
                    + $"fill (fill {fill:F2}:1, outline {outline:F2}:1). Below 3:1 the switch has "
                    + "no visible extent — there is nothing on screen to aim at.")
                .That(best).IsStrictlyGreaterThan(3.0);
        }
    }

    /// <summary>
    /// The knob's SIDE is one of the three carriers that make this control readable without
    /// colour, so the knob has to be visible on the track it slides along — and by its own fill it
    /// is not: TextPrimary on ToggleOn measures 2.91 / 3.53 / 1.37 across
    /// Midnight / Ember / Contrast. The dark <see cref="Tokens.BrushSurfaceDark"/> ring is what
    /// carries it (4.37 / 4.18 / 15.30 on ToggleOn), so this gates max(fill, ring) at 3:1 — the
    /// same fill-plus-outline shape the track and the dwell bar are gated with, and for the same
    /// reason: whichever of the two reads better is what the eye actually uses.
    ///
    /// Cross-theme half: <c>Theme.ToggleContrastTests.TheKnob_IsPerceptibleAgainstEitherTrackFill</c>.
    /// </summary>
    [StaFact]
    public void TheKnobIsPerceptibleAgainstTheTrackFill()
    {
        foreach (var initial in new[] { true, false })
        {
            var toggle = Build(initial);

            var trackFill = ((SolidColorBrush)toggle.Track.Background).Color;
            var fill = Contrast(((SolidColorBrush)toggle.Knob.Background).Color, trackFill);
            var ring = toggle.Knob.BorderBrush is SolidColorBrush b
                ? Contrast(b.Color, trackFill)
                : 1.0; // no ring at all -> no rescue, which is the defect exactly
            var best = Math.Max(fill, ring);

            Check.WithCustomMessage(
                    $"state={initial}: the knob reaches only {best:F2}:1 against the track it sits "
                    + $"on (fill {fill:F2}:1, ring {ring:F2}:1). Below 3:1 the knob's side stops "
                    + "carrying state, and this control is left saying it in colour alone.")
                .That(best).IsStrictlyGreaterThan(3.0);
        }
    }

    /// <summary>
    /// The card's border inset must be cancelled EXACTLY, and this is the theme-independent half
    /// of that guard.
    ///
    /// <para>
    /// A <see cref="Border"/> deflates its child by its <c>BorderThickness</c>, so a bordered
    /// 100 DIP card hands the switch a 98 DIP slot for its explicit 100 — and WPF does not split
    /// the shortfall: <c>ComputeAlignmentOffset</c> demotes Stretch to Top, the switch is pushed
    /// DOWN, and all of it lands on the bottom edge, where the 6 DIP dwell bar is. The row Grid's
    /// negative vertical margin is what gives the switch its 100 back.
    /// </para>
    /// <para>
    /// Asserted as an EQUALITY against the card's own thickness rather than against a literal,
    /// because the thickness is a theme token: 1 in Midnight and Ember, 2 in Contrast. A
    /// compensation that merely happens to match today's ambient theme would leave the Contrast
    /// theme — the one built for low vision — short by a DIP.
    /// </para>
    /// <para>
    /// This is the gate with teeth. <see cref="TheSwitchIsNeverDrawnSmallerThanItsHitCircle"/>
    /// catches the same defect through its effect (the layout clip) and
    /// <see cref="TheDwellBarPaintsThroughTheWholeCard"/> catches the worst of it in pixels, but
    /// only this one is exact and only this one is blind to which theme is ambient.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheCardsBorderInsetIsCancelledExactly()
    {
        var toggle = Build(initial: true);
        var card = toggle.RowSurface;
        var row = (Grid)card.Child;

        Check.WithCustomMessage(
                $"the row Grid's vertical margin is {row.Margin.Top}/{row.Margin.Bottom} against a "
                + $"card border of {card.BorderThickness.Top}/{card.BorderThickness.Bottom}. It has "
                + "to be the exact negation, or the switch is handed a slot shorter than the 100 DIP "
                + "circle it registers and the shortfall lands on its dwell bar.")
            .That((row.Margin.Top, row.Margin.Bottom))
            .IsEqualTo((-card.BorderThickness.Top, -card.BorderThickness.Bottom));

        Check.WithCustomMessage(
                $"the row Grid's horizontal margin is {row.Margin.Left}/{row.Margin.Right}, not 0. "
                + "The cancellation is vertical only, on purpose: horizontally there is no shortfall "
                + "to make up, and a negative margin there would push the switch out over the card's "
                + "own outline.")
            .That((row.Margin.Left, row.Margin.Right)).IsEqualTo((0.0, 0.0));
    }

    /// <summary>
    /// The dwell bar's Accent has to reach the SCREEN, through the whole stack the composite puts
    /// in its way: the card and its border, the row Grid and its negative margin, the switch and
    /// its own outline, and the rounded clip <c>DwellButton</c> installs over its layout. Every
    /// other test in this file reads a DP, a brush or a transform, so a bar that is present in the
    /// visual tree and paints NOTHING satisfies all of them — the colour-swatch defect exactly.
    ///
    /// <para>
    /// Matched on COLOUR, not alpha: the card's fill is opaque, so an alpha probe reads 255
    /// whether or not the bar drew. The floor comes from
    /// <see cref="DwellButton.ProgressBarHeight"/> less the bar's two halo rows, so it follows the
    /// constant rather than pinning a number that would go stale.
    /// </para>
    /// <para>
    /// Its LIMIT, stated because it was not obvious: this probe renders under the ambient theme,
    /// and at <c>BorderThicknessStandard</c> 1 the bar survives even with the compensation deleted
    /// — the switch's own 2 DIP outline has already lifted it clear of the bottom edge, so the
    /// clip cuts padding rather than bar. Measured: 602 Accent pixels over 4 rows both with and
    /// without the compensation at thickness 1, and 455 over 3 rows with a thickness-2 card border
    /// and no compensation. So this test alone would NOT have caught the regression;
    /// <see cref="TheCardsBorderInsetIsCancelledExactly"/> is the half that does, and this one
    /// proves the pixels are really there.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheDwellBarPaintsThroughTheWholeCard()
    {
        var registrar = new FakeRegistrar { Progress = 1.0 };
        var toggle = Build(registrar, initial: true);

        // Arranged standalone at the narrowest real content column, with the bottom margin zeroed,
        // so bitmap rows map 1:1 onto the card's own box — the same technique (and the same reason)
        // as DwellButtonProgressTests' silhouette probe.
        var card = toggle.RowSurface;
        card.Margin = new Thickness(0);
        card.Width = 416;
        card.Measure(new Size(card.Width, card.Height));
        card.Arrange(new Rect(0, 0, card.Width, card.Height));
        card.UpdateLayout();
        toggle.Switch.UpdateVisual();

        var w = (int)Math.Round(card.Width);
        var h = (int)Math.Round(card.Height);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(card);

        var stride = w * 4;
        var pixels = new byte[stride * h];
        rtb.CopyPixels(pixels, stride, 0);

        var accent = Tokens.Accent;
        var total = 0;
        var rows = 0;
        for (var y = 0; y < h; y++)
        {
            var inRow = 0;
            for (var x = 0; x < w; x++)
            {
                var i = y * stride + x * 4;
                if (pixels[i + 2] == accent.R && pixels[i + 1] == accent.G && pixels[i] == accent.B)
                    inRow++;
            }

            total += inRow;
            if (inRow > 0) rows++;
        }

        var floor = (int)DwellButton.ProgressBarHeight - 2;
        Check.WithCustomMessage(
                $"at full dwell the card renders {total} Accent pixels over {rows} rows, under the "
                + $"{floor} rows a {DwellButton.ProgressBarHeight} DIP bar draws inside its halo. "
                + "Either the bar is not reaching the screen at all — present in the tree but "
                + "painting nothing, which no other test here can see — or something above it is "
                + "cutting rows off the bottom. If it is the card's border, cancel its inset on the "
                + "row Grid; do not delete the outline and do not grow the card past 100 DIP.")
            .That(rows).IsGreaterOrEqualThan(floor);
    }

    /// <summary>
    /// The switch is the thing the user aims at, so it must read as an object against the card it
    /// sits on — and by fill alone it does not: <see cref="Tokens.SurfaceLight"/> on
    /// <see cref="Tokens.SurfaceMedium"/> is a 1.23 / 1.23 / 1.14 step across
    /// Midnight / Ember / Contrast, i.e. no boundary at all. Its
    /// <see cref="Tokens.BrushTextSecondary"/> outline measures 5.63 / 6.79 / 15.04 there.
    ///
    /// <para>
    /// This gate is why the outline exists, and it is the half that was MISSING when the card's
    /// border was deleted to dodge a layout clip: nothing in the suite noticed the row losing its
    /// edges, because every other test here reads a colour pair that still held. Same
    /// max(fill, outline) shape as the track, the knob and the dwell bar.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheSwitchHasAPerceptibleBoundaryAgainstTheCard()
    {
        var toggle = Build(initial: true);
        var root = toggle.Switch.Root;

        var card = ((SolidColorBrush)toggle.RowSurface.Background).Color;
        var fill = Contrast(((SolidColorBrush)root.Background).Color, card);
        var outline = root.BorderThickness.Top > 0 && root.BorderBrush is SolidColorBrush b
            ? Contrast(b.Color, card)
            : 1.0; // no outline drawn at all -> the fill has to carry it, and it cannot
        var best = Math.Max(fill, outline);

        Check.WithCustomMessage(
                $"the switch reaches only {best:F2}:1 against the card it sits on (fill "
                + $"{fill:F2}:1, outline {outline:F2}:1 at thickness {root.BorderThickness.Top}). "
                + "Below 3:1 the dwell target has no edge — the user is aiming at a smudge.")
            .That(best).IsStrictlyGreaterThan(3.0);
    }

    /// <summary>
    /// The row card's own boundary against the page behind it, which is
    /// <see cref="Tokens.SurfaceDark"/>. Palette rule 1 puts this on the BORDER rather than the
    /// fill — "the border carries the boundary; the fill only suggests depth",
    /// docs/design/overlay-unification-spec.md:284-286 — and the numbers are why: SurfaceMedium on
    /// SurfaceDark is a 1.23 / 1.23 / 1.14 step, while BorderSubtle on it measures
    /// 3.35 / 3.32 / 21.00.
    ///
    /// <para>
    /// The palette half of this is already gated outside the suite, by
    /// tools/palette_validate.py:352-365 ("GATE 1 - layering"), which checks BorderSubtle against
    /// the worst composited surface in every theme. What that cannot see is whether this CONTROL
    /// draws a border at all — and for one commit it did not, which took the row from a delineated
    /// object (the old full-bleed ToggleOn fill read 4.37:1 against the page) to a 1.23:1
    /// nothing. That is the gap this test closes.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheRowCardHasAPerceptibleBoundaryAgainstThePage()
    {
        var toggle = Build(initial: false);
        var card = toggle.RowSurface;

        var page = Tokens.SurfaceDark;
        var fill = Contrast(((SolidColorBrush)card.Background).Color, page);
        var outline = card.BorderThickness.Top > 0 && card.BorderBrush is SolidColorBrush b
            ? Contrast(b.Color, page)
            : 1.0; // no border -> only the fill step is left, and it is ~1.2:1
        var best = Math.Max(fill, outline);

        Check.WithCustomMessage(
                $"the row card reaches only {best:F2}:1 against the settings page (fill "
                + $"{fill:F2}:1, border {outline:F2}:1 at thickness {card.BorderThickness.Top}). "
                + "Palette rule 1 puts the boundary on the border for exactly this reason; a "
                + "borderless card on SurfaceMedium is invisible. If the border was removed to "
                + "stop it eating the dwell bar, cancel the inset on the row Grid instead.")
            .That(best).IsStrictlyGreaterThan(3.0);
    }

    // ────── T3: the label's own contrast, where it now lives ──────

    /// <summary>
    /// The 2.91:1 this task exists to fix. The label is no longer drawn on the state fill at all,
    /// so the pairing that matters is label-on-row-surface, which measures 10.33 / 12.01 / 18.42.
    /// The bar is 4.5:1, not 3:1: <see cref="Tokens.FontSizeTitle"/> is 24 DIP, which IS WCAG
    /// large text — but the label wraps and this asserts the stricter bar anyway, since clearing
    /// it costs nothing here and a future font-size reduction would otherwise pass silently.
    /// </summary>
    [StaFact]
    public void TheLabelSitsOnTheRowSurface_AndClearsFourAndAHalfToOneThere()
    {
        var toggle = Build(initial: true);

        var fg = ((SolidColorBrush)toggle.Label.Foreground).Color;
        var bg = ((SolidColorBrush)toggle.RowSurface.Background).Color;
        var ratio = Contrast(fg, bg);

        Check.WithCustomMessage(
                $"the label reads {ratio:F2}:1 against the surface it is drawn on. On the ON state "
                + "fill it measured 2.91:1, which is what moving it onto the row surface fixed — "
                + "putting it back on a state fill undoes the whole point of this control.")
            .That(ratio).IsStrictlyGreaterThan(4.5);

        // The structural half: the surface under the label must not be the state fill, whatever
        // the numbers happen to be. A theme whose ToggleOn cleared 4.5:1 would satisfy the ratio
        // above while restoring the anatomy this task replaced.
        Check.WithCustomMessage(
                "the row surface now paints the state colour, so the label is back on the fill it "
                + "was moved off.")
            .That(bg).IsNotEqualTo(Tokens.ToggleOn);
        Check.WithCustomMessage(
                "the row surface now paints the OFF state colour, so the label is back on the fill "
                + "it was moved off.")
            .That(bg).IsNotEqualTo(Tokens.ToggleOff);
    }

    // ────── T4: the state word's own contrast ──────

    /// <summary>
    /// <see cref="Tokens.FontSizeSubtitle"/> is 18 DIP at normal weight, which is NOT WCAG large
    /// text (that wants >= 24 DIP, or >= 18.66 DIP bold), so the state word owes 4.5:1 rather than
    /// 3:1. Against the switch's <see cref="Tokens.SurfaceLight"/> fill,
    /// <see cref="Tokens.BrushTextSecondary"/> measures 4.38 in Midnight and FAILS;
    /// <see cref="Tokens.BrushTextPrimary"/> measures 8.03 / 9.52 / 15.13 and clears.
    ///
    /// So the word is TextPrimary in BOTH states, and <c>RefreshVisual</c> swaps no foreground at
    /// all: state is already carried three ways (the word itself, the knob's side, the track's
    /// fill), and a colour swap that costs legibility adds nothing to that.
    /// </summary>
    [StaFact]
    public void TheStateWordClearsFourAndAHalfToOneAgainstTheSwitchFill()
    {
        foreach (var initial in new[] { true, false })
        {
            var toggle = Build(initial);

            var fg = ((SolidColorBrush)toggle.StateText.Foreground).Color;
            var bg = ((SolidColorBrush)toggle.Switch.Root.Background).Color;
            var ratio = Contrast(fg, bg);

            Check.WithCustomMessage(
                    $"state={initial}: the word \"{toggle.StateText.Text}\" reads {ratio:F2}:1 "
                    + $"against the switch fill at {toggle.StateText.FontSize} DIP normal weight, "
                    + "which is not WCAG large text — the bar is 4.5:1. This word is the primary "
                    + "carrier of state for a user who cannot rely on the track's colour.")
                .That(ratio).IsStrictlyGreaterThan(4.5);
        }
    }

    // ────── T5: activation through the registrar, not through Toggle() ──────

    /// <summary>
    /// Every test above drives <c>Toggle()</c> directly, so a control whose registered callback
    /// pointed somewhere else entirely — or nowhere — would satisfy all of them. This fires the
    /// delegate the REGISTRAR holds, which is the only path a gaze user has, and resolves it
    /// through <see cref="IDwellComposite.Children"/> so the composite's own view of its targets
    /// is on the hook too.
    /// </summary>
    [StaFact]
    public void ActivationFlowsThroughTheRegistrar()
    {
        var registrar = new FakeRegistrar();
        var toggle = Build(registrar, initial: false);
        toggle.Attach(new StackPanel());

        var target = toggle.Children.Single().DwellId;
        Check.WithCustomMessage($"nothing is registered under the child's id '{target}'.")
            .That(registrar.Callbacks.ContainsKey(target)).IsTrue();

        registrar.Callbacks[target]();

        Check.WithCustomMessage(
                "a completed dwell on the switch did not flip the state. The registered callback "
                + "is the only activation path a gaze user has.")
            .That(toggle.StateText.Text).IsEqualTo("On");
        Check.That(((SolidColorBrush)toggle.Track.Background).Color).IsEqualTo(Tokens.ToggleOn);
        Check.That(toggle.Knob.HorizontalAlignment).IsEqualTo(HorizontalAlignment.Right);
    }

    // ────── The UIA anchor ──────

    /// <summary>
    /// A <see cref="Border"/> has no <c>AutomationPeer</c> in WPF, so it never appears in the UIA
    /// tree and cannot carry the <c>Settings_Toggle_{id}</c> automation id the App's UI tests find
    /// the control by. The state word is the innermost element inside the switch that CAN carry it.
    /// <c>AppFixture.SimulateDwellOn</c> dwells an element's bounding-rect centre, so what matters
    /// is where that centre lands.
    ///
    /// <para>
    /// TWO assertions, and the first is the load-bearing one. The word's BOX is stretched across
    /// the switch's whole content width — its glyphs are placed by Padding, which sits inside the
    /// box — so the anchor's centre IS the switch's centre by construction rather than by an
    /// offset that happens to fall inside the circle. An earlier draft sized the word to its text
    /// and measured 40.11 DIP of 50: inside, but on a 20% margin that any change to the word or
    /// the track's width would erode. Switching the Padding back to a Margin shrinks the box again
    /// and fails the span assertion, which the distance assertion alone would not catch.
    /// </para>
    /// <para>
    /// The units are sound in both directions: the fixture reads the UIA bounding rect in physical
    /// pixels and the registered radius is <c>DpiHelper.LogicalToPhysicalX(ControlBtnRadius)</c>,
    /// so the two scale together and comparing them in DIP here is valid.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheUiaAnchorsCentre_FallsInsideTheSwitchesHitCircle()
    {
        var toggle = Build(initial: true);
        AttachAndLayout(toggle);

        var anchor = toggle.StateText;
        Check.WithCustomMessage(
                "the automation id is not on the state word any more. Whatever now carries it must "
                + "have an AutomationPeer (a Border does not) and must sit inside the switch's hit "
                + "circle — repoint this test at it.")
            .That(System.Windows.Automation.AutomationProperties.GetAutomationId(anchor))
            .IsEqualTo("Settings_Toggle_id");

        var root = toggle.Switch.Root;

        // The span is what puts the centre on the centre. Derived from the switch's own box less
        // its own outline, not restated, so neither number can drift out from under this.
        var contentWidth = root.ActualWidth - root.BorderThickness.Left - root.BorderThickness.Right;
        Check.WithCustomMessage(
                $"the UIA anchor is {anchor.ActualWidth:F2} DIP wide inside a {contentWidth:F2} DIP "
                + "switch, so it no longer spans it and its bounding-rect centre is an offset "
                + "rather than the switch's centre. Place the state word's glyphs with Padding "
                + "(inside the box) rather than Margin (outside it).")
            .That(anchor.ActualWidth).IsEqualTo(contentWidth);

        var offset = anchor.TransformToAncestor(root)
            .Transform(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
        var centre = new Point(root.ActualWidth / 2, root.ActualHeight / 2);
        var distance = Math.Sqrt(
            Math.Pow(offset.X - centre.X, 2) + Math.Pow(offset.Y - centre.Y, 2));

        Check.WithCustomMessage(
                $"the UIA anchor's centre is {distance:F2} DIP from the switch's centre, outside "
                + $"the {Radius} DIP hit circle. A UI test dwelling that point — and a user aiming "
                + "at the word — would activate nothing.")
            .That(distance).IsStrictlyLessThan(Radius);
    }

    // ────── WCAG helpers ──────

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

    /// <summary>
    /// Records every registration and the callback it carried, and reports whatever progress the
    /// test sets — so these tests drive the real <see cref="DwellControl.UpdateVisual"/> and the
    /// real activation path rather than a test-only hook on the production type.
    /// </summary>
    private class FakeRegistrar : IDwellRegistrar
    {
        public double Progress { get; set; }

        public List<string> Registered { get; } = [];

        public Dictionary<string, Action> Callbacks { get; } = [];

        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated)
        {
            Registered.Add(id);
            Callbacks[id] = onActivated;
        }

        public void Unregister(string id, DwellCategory category)
        {
            Registered.Remove(id);
            Callbacks.Remove(id);
        }

        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }

        public double GetProgress(string id, DwellCategory category) => Progress;
    }
}
