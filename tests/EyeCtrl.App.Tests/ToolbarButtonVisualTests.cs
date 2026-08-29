namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The toolbar's dwell target is a circle of radius Size/2. These tests pin the properties that
/// make the drawn button honest about that — it is an Ellipse of exactly that size, it carries a
/// rim visible against the strip, and its progress is an arc rather than a 4 px edge bar.
///
/// Layout is measured against the hosting Canvas, never with TranslatePoint against the Window:
/// a Window's content is not visually connected until Show(), so TranslatePoint returns (0,0) for
/// every element and any "nothing moved" assertion built on it is vacuously green.
///
/// Nothing calls Show(). Constructing the window parses its XAML, which is all these need.
/// </summary>
[Collection("WpfApplication")]
public class ToolbarButtonVisualTests
{
    private const double ButtonSize = 120;

    private static ToolbarWindow NewInitialisedToolbar()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ToolbarWindow();
        window.Initialize(ButtonSize, 700);
        return window;
    }

    private static Canvas ButtonCanvas(ToolbarWindow w) => w.ButtonCanvasForTest;

    // Filtered on Fill, not on Width: a DwellArcButton contributes two Ellipses, and the one to
    // exclude is the RimHalo, which is a stroke-only ring with no fill. A `Width == ButtonSize`
    // filter would also work today, but only because RimHaloOffset happens to be non-zero — it
    // would silently start matching both ellipses if that offset ever went to 0.
    private static List<Ellipse> Faces(ToolbarWindow w) =>
        ButtonCanvas(w).Children.OfType<Ellipse>().Where(e => e.Fill is not null).ToList();

    [StaFact]
    public void EveryButton_IsAnEllipseOfExactlyTheDwellDiameter()
    {
        var faces = Faces(NewInitialisedToolbar());

        Check.WithCustomMessage("Six rendered buttons expected.").That(faces).HasSize(6);
        foreach (var f in faces)
        {
            // A square tile hid ~21.5% dead area outside the inscribed hit circle. An Ellipse of
            // the dwell diameter makes the drawing and the hit test the same shape.
            Check.That(f.Width).IsEqualTo(ButtonSize);
            Check.That(f.Height).IsEqualTo(ButtonSize);
        }
    }

    [StaFact]
    public void EveryButton_CarriesAVisibleRim()
    {
        // SurfaceMedium on SurfaceDark computes to 1.23:1 — without a rim the boundary between
        // adjacent targets is invisible. DwellArcButton's default stroke is BrushBorderSubtle.
        foreach (var f in Faces(NewInitialisedToolbar()))
        {
            var rim = ((SolidColorBrush)f.Stroke).Color;
            Check.That(rim == Tokens.BorderSubtle || rim == Tokens.AccentWarning).IsTrue();
            Check.That(f.StrokeThickness).IsStrictlyGreaterThan(0.0);
        }
    }

    [StaFact]
    public void EveryButton_DrawsAnIcon_AndNoTextOnItsFace()
    {
        var canvas = ButtonCanvas(NewInitialisedToolbar());

        // Only the ScaleTransform-carrying Paths, not every Path: DwellArcButton contributes a
        // HaloPath and a ProgressPath to each button whether or not it was given an icon, so a
        // bare Path count reaches 12 here even with all six icons missing — a bar of 5 that
        // nothing can fail. The icon layers are the ones scaled from the 24-unit authoring grid
        // (DwellArcButton.Create's BuildLayer), and they are the only ones.
        //
        // Counted as DISTINCT Canvas.Left values rather than as layers, because an icon may have
        // a fill layer, a stroke layer or both, and all of a button's layers share one left edge
        // (DwellArcButton.cs:303). Six distinct lefts means six buttons drew an icon; a raw layer
        // count could be reached by five two-layer icons and one missing one.
        var iconLefts = canvas.Children.OfType<Path>()
            .Where(p => p.RenderTransform is ScaleTransform)
            .Select(Canvas.GetLeft)
            .Distinct()
            .ToList();

        Check.WithCustomMessage("A button fell back to a glyph — no icon paths.")
            .That(iconLefts).HasSize(6);

        var visibleText = canvas.Children.OfType<TextBlock>().Where(t => t.Opacity > 0).ToList();
        Check.WithCustomMessage("This design has no text on the button faces.")
            .That(visibleText).IsEmpty();
    }

    [StaFact]
    public void EveryButton_KeepsATextBlockAutomationAnchor()
    {
        // WPF's UIA control view does not expose Ellipse or Path from a transparent overlay
        // window, so the AutomationId has to live on a TextBlock. Losing it silently deletes
        // Toolbar_Button_<Id> from the UIA tree, which is how AppFixture finds these buttons
        // (AppFixture.cs:193-196, ToolbarTests.cs:35).
        var ids = ButtonCanvas(NewInitialisedToolbar()).Children.OfType<TextBlock>()
            .Select(AutomationProperties.GetAutomationId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        Check.That(ids).Contains("Toolbar_Button_Pause", "Toolbar_Button_SelectionMode",
            "Toolbar_Button_Keyboard", "Toolbar_Button_Settings",
            "Toolbar_Button_TaskSwitcher", "Toolbar_Button_ResetAdaptation");
    }

    /// <summary>
    /// The anchor's box must be the disc's box, because the anchor is what publishes the button's
    /// BoundingRectangle to UIA and dwell simulation aims at that rectangle's centre
    /// (AppFixture.SimulateDwellOn, AppFixture.cs:259-265). An unsized TextBlock in a Canvas
    /// measures to its own text, so the rect became a ~16px sliver at the disc's top-left whose
    /// width tracked the length of the id — putting the aim point 54-69 px from the true centre
    /// against a dwell radius of 60, i.e. outside the target for the three shortest ids. Those
    /// tests are Category=UI, so the headless suite never saw it.
    ///
    /// Still unproven here, and deliberately not claimed: that UIA actually EXPOSES an
    /// Opacity = 0 TextBlock from a transparent overlay window. WPF's automation peers key off
    /// Visibility/IsVisible rather than Opacity, and a non-empty Text satisfies
    /// IsControlElementCore — but that is reasoning, not evidence, and only the real tracker can
    /// settle it. Task 10 carries the confirmation.
    /// </summary>
    [StaFact]
    public void EveryAutomationAnchor_CoversItsOwnDisc()
    {
        var window = NewInitialisedToolbar();
        var canvas = ButtonCanvas(window);

        var anchors = canvas.Children.OfType<TextBlock>()
            .Where(t => !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(t)))
            .OrderBy(Canvas.GetLeft)
            .ToList();
        Check.That(anchors).HasSize(6);

        foreach (var anchor in anchors)
        {
            var id = AutomationProperties.GetAutomationId(anchor);

            Check.WithCustomMessage($"{id} is sized to its text, not to its button.")
                .That(anchor.Width).IsEqualTo(ButtonSize);
            Check.That(anchor.Height).IsEqualTo(ButtonSize);
        }

        // Paired left-to-right against the discs, NOT "is centred on some disc": membership lets
        // a permutation through, and a permutation is precisely the defect that would make dwell
        // simulation activate a button other than the one the id names. Pinning the id sequence
        // is what makes the pairing — and this test's name — mean anything.
        Check.That(anchors.Select(AutomationProperties.GetAutomationId))
            .ContainsExactly("Toolbar_Button_Pause", "Toolbar_Button_SelectionMode",
                "Toolbar_Button_Keyboard", "Toolbar_Button_Settings",
                "Toolbar_Button_TaskSwitcher", "Toolbar_Button_ResetAdaptation");

        foreach (var (anchor, face) in anchors.Zip(Faces(window).OrderBy(Canvas.GetLeft)))
        {
            var id = AutomationProperties.GetAutomationId(anchor);
            var anchorCentre = new Point(Canvas.GetLeft(anchor) + anchor.Width / 2,
                                         Canvas.GetTop(anchor) + anchor.Height / 2);
            var discCentre = new Point(Canvas.GetLeft(face) + ButtonSize / 2,
                                       Canvas.GetTop(face) + ButtonSize / 2);

            Check.WithCustomMessage($"{id}'s anchor is not centred on its OWN disc, so a dwell "
                    + "aimed at its published rectangle would land on a different button.")
                .That(anchorCentre).IsEqualTo(discCentre);
        }
    }

    [StaFact]
    public void ButtonCentres_AndCanvasSize_AreExactlyAsLaidOut()
    {
        var window = NewInitialisedToolbar();
        var canvas = ButtonCanvas(window);
        var lefts = Faces(window).Select(Canvas.GetLeft).OrderBy(x => x).ToList();

        // Pinned exactly rather than as "at most two distinct pitches": that weaker form passes
        // if every gap is uniformly wrong, and passes if the deliberate extra gap before Reset
        // vanishes entirely. At ButtonSize 120 the arithmetic is fully determined — margin 12,
        // pitch 144, and a 24 px ResetGap that makes the last step 168.
        Check.That(lefts).ContainsExactly(12.0, 156.0, 300.0, 444.0, 588.0, 756.0);
        Check.That(canvas.Width).IsEqualTo(888.0);
        Check.That(canvas.Height).IsEqualTo(144.0);
    }
}
