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
/// <c>DwellProgressContrastTests</c> established.
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
    /// </summary>
    [StaFact]
    public void TheSwitchIsNeverDrawnSmallerThanItsHitCircle()
    {
        var toggle = Build(initial: true);

        Check.WithCustomMessage(
                $"the switch is drawn {toggle.Switch.Root.Width}x{toggle.Switch.Root.Height} DIP, "
                + "inside the 100 DIP circle it registers — the user would be activating it from "
                + "points where nothing is drawn.")
            .That(Math.Min(toggle.Switch.Root.Width, toggle.Switch.Root.Height))
            .IsGreaterOrEqualThan(100.0);
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

        AttachAndLayout(toggle);
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Check.WithCustomMessage(
                $"the switch's content wants {content.DesiredSize.Width:F2} DIP but the switch is "
                + $"drawn {toggle.Switch.Root.Width} DIP wide. DwellButton clips its layout to the "
                + "rounded silhouette, so the overflow is cut, not drawn — the track or the state "
                + "word loses its edge with nothing thrown.")
            .That(content.DesiredSize.Width).IsLessOrEqualThan(toggle.Switch.Root.Width);
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
    /// the control by. The state word is the innermost element inside the switch that CAN carry
    /// it — but it is not centred in the switch, so this pins the thing that actually matters:
    /// the anchor's centre, which is where <c>AppFixture.SimulateDwellOn</c> puts the cursor, must
    /// fall inside the hit circle the switch registers. Off by more than the radius and the UI
    /// test dwells a point that activates nothing.
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
