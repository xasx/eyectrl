namespace EyeCtrl.App.Tests.Settings;

using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

public class SettingsRowLayoutTests
{
    /// <summary>
    /// The narrowest supported content column, in DIP: 0.9 x an 800 DIP display, minus the 240
    /// sidebar, minus ContentArea's 16-per-side margin, minus ContentCard's 16-per-side padding.
    /// The same budget SettingsAutoPaginationTests and SettingsContentFitTests measure against.
    /// </summary>
    private const double NarrowColumnWidth = 416.0;

    /// <summary>
    /// The content column on the 1920x1080 display this application is actually used on:
    /// 1728 - 240 - 32 - 32. Deliberately used for every two-column assertion — running those at
    /// <see cref="NarrowColumnWidth"/> would silently assert against the STACKED form instead.
    /// </summary>
    private const double WideColumnWidth = 1424.0;

    private const double RadiusPhysical = 50;
    private const int DwellMs = 800;

    private static SliderSpec Slider(string? description = null) =>
        new("sl", "Smoothing Factor", 0, 1, 0.1, 1, () => 0.5, _ => { })
        {
            Description = description
        };

    /// <summary>
    /// A host panel that measures the way the real content panel does: pinned to the column's
    /// width and carrying the window's font. SettingsWindow is an OverlayWindow, whose
    /// constructor sets FontFamily = Tokens.FontStandard and lets it inherit down; without that
    /// the measurement runs in the system font and reports widths the application never produces.
    /// </summary>
    private static StackPanel Host(double columnWidth)
    {
        var panel = new StackPanel { Width = columnWidth };
        TextElement.SetFontFamily(panel, Tokens.FontStandard);
        return panel;
    }

    /// <summary>Builds one row through production's single construction path and attaches it.</summary>
    private static FrameworkElement AttachRow(SettingsRowSpec spec, StackPanel host)
    {
        var built = SettingsWindow.CreateControl(
            spec, new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
            host.Width);
        ((IDwellComposite)built).Attach(host);

        Check.WithCustomMessage(
                "A labelled row must attach exactly ONE element to the content panel. Two means "
                + "the name is stacked on the control as a separate child again, which is the "
                + "layout this row type replaced.")
            .That(host.Children.Count).IsEqualTo(1);

        return (FrameworkElement)host.Children[0];
    }

    /// <summary>Every TextBlock in the arranged visual tree, in tree order.</summary>
    private static List<TextBlock> TextBlocksIn(DependencyObject root)
    {
        var found = new List<TextBlock>();
        void Walk(DependencyObject d)
        {
            if (d is TextBlock tb) found.Add(tb);
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < count; i++)
                Walk(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
        }

        Walk(root);
        return found;
    }

    /// <summary>Measures AND arranges, which walking the visual tree requires.</summary>
    private static void LayOut(StackPanel host)
    {
        host.Measure(new Size(host.Width, double.PositiveInfinity));
        host.Arrange(new Rect(new Size(host.Width, host.DesiredSize.Height)));
    }

    [Fact]
    public void ARowSpec_CarriesAnOptionalDescription()
    {
        var without = new ToggleSpec("t", "Start Paused", () => false, _ => { });
        var with = new ToggleSpec("t", "Start Paused", () => false, _ => { })
        {
            Description = "EyeCtrl starts with tracking suspended."
        };

        Check.That(without.Description).IsNull();
        Check.That(with.Description).IsEqualTo("EyeCtrl starts with tracking suspended.");
    }

    /// <summary>
    /// The shape of the whole change: the setting's name and the control that changes it are on
    /// ONE line, in a two-column Grid — not a heading stacked above a control, which is what
    /// every labelled row was before.
    /// </summary>
    [StaFact]
    public void ALabelledRow_PutsTheNameBesideTheControl()
    {
        var host = Host(WideColumnWidth);
        var root = AttachRow(Slider(), host);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "A labelled row on a {0:F0} DIP column attached a {1} rather than a Grid, so the "
                + "name is no longer beside its control. Two-column is the whole point of the row "
                + "type; if the threshold moved, move this test's column with it.",
                WideColumnWidth, root.GetType().Name))
            .That(root).IsInstanceOf<Grid>();

        Check.WithCustomMessage(
                "The two-column row's Grid does not have exactly two columns — a name column and "
                + "a control column.")
            .That(((Grid)root).ColumnDefinitions.Count).IsEqualTo(2);
    }

    /// <summary>
    /// A row with a description draws both lines. The description is the reason
    /// <c>SettingsRowSpec.Description</c> exists; a row type that accepted it and dropped it
    /// would look identical to one that had never been given it.
    /// </summary>
    [StaFact]
    public void ALabelledRow_DrawsBothItsNameAndItsDescription()
    {
        const string description = "How hard the cursor is smoothed against gaze jitter.";
        var host = Host(WideColumnWidth);
        var root = AttachRow(Slider(description), host);
        LayOut(host);

        var texts = TextBlocksIn(root).Select(t => t.Text).ToList();

        Check.WithCustomMessage(
                "The row does not draw the setting's name. Drawn text: "
                + string.Join(" | ", texts))
            .That(texts).Contains("Smoothing Factor");

        Check.WithCustomMessage(
                "The row was given a Description and did not draw it. Drawn text: "
                + string.Join(" | ", texts))
            .That(texts).Contains(description);

        // WHICH brush, not merely that something was drawn. Colour rather than alpha or
        // reference: an opaque brush reads alpha 255 whichever token it came from.
        // BrushTextDim would fail 4.5:1 on the card's SurfaceMedium in Midnight (4.37) and this
        // is body text at normal weight, so it owes the full 4.5 — see
        // SettingsRowDescriptionContrastTests for the per-theme numbers.
        var drawn = TextBlocksIn(root).Single(t => t.Text == description);
        var colour = ((System.Windows.Media.SolidColorBrush)drawn.Foreground).Color;

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The description is drawn in {0} rather than Tokens.TextSecondary ({1}). "
                + "TextSecondary is the dimmest token that clears the 4.5:1 WCAG AA floor for "
                + "normal-weight text on the content card in EVERY theme; TextDim measures 4.37 "
                + "in Midnight and fails. This is text the user has to read with the only input "
                + "channel they have.",
                colour, Tokens.TextSecondary))
            .That(colour).IsEqualTo(Tokens.TextSecondary);
    }

    /// <summary>
    /// And a row WITHOUT one draws no second line at all. An empty TextBlock still takes a line
    /// box, so constructing one unconditionally would push every row in the application taller
    /// for nothing — and pagination measures those heights.
    /// </summary>
    [StaFact]
    public void ARowWithoutADescription_DrawsNoSecondLine()
    {
        var host = Host(WideColumnWidth);
        var root = AttachRow(Slider(description: null), host);
        LayOut(host);

        var texts = TextBlocksIn(root).Select(t => t.Text).ToList();

        Check.WithCustomMessage("The row does not draw the setting's name.")
            .That(texts).Contains("Smoothing Factor");

        var blank = texts.Where(string.IsNullOrWhiteSpace).ToList();
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "A row with no Description drew {0} blank or whitespace TextBlock(s). An empty one "
                + "still occupies a line box, so it grows every such row and re-paginates every "
                + "page holding one.",
                blank.Count))
            .That(blank).IsEmpty();
    }

    /// <summary>
    /// THE reason this row type exists, and the only assertion here that is about the user rather
    /// than the layout.
    ///
    /// <para>
    /// A dwell target is registered at its control's CENTRE with a fixed radius, so the clearance
    /// between the circles of two vertically adjacent controls is the row pitch minus one
    /// diameter — nothing else. At a 100 DIP pitch two 100 DIP circles are exactly tangent and a
    /// one-pixel gaze drift picks the neighbour. The floor is DERIVED from the colour-swatch
    /// grid, the only adjacent-target pitch in the application already pinned by a geometry test
    /// (SwatchTargetGeometryTests), rather than restated as a number this test could adapt to.
    /// </para>
    /// <para>
    /// Measured through <c>SettingsAutoPagination.MeasureRowHeight</c> — the MEASUREMENT path,
    /// not the renderer. That is deliberate: pagination and rendering share
    /// <c>CreateControl</c> precisely so the two cannot disagree, and if row assembly ever moves
    /// out of <c>CreateControl</c> into <c>RenderSpec</c> this is where it shows.
    /// </para>
    /// </summary>
    [StaFact]
    public void ALabelledRow_LeavesAtLeastTheSwatchGridsClearanceBetweenHitCircles()
    {
        var swatchClearance =
            DwellColorPicker.SwatchHeight + (2 * DwellColorPicker.SwatchVerticalMargin)
            - (2 * SettingsWindow.ColorSwatchRadius);

        var measured = SettingsAutoPagination.MeasureRowHeight(
            Slider(), WideColumnWidth, RadiusPhysical, DwellMs);
        var clearance = measured - (2 * SettingsWindow.ControlBtnRadius);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "A labelled settings row measures {0:F2} DIP tall, leaving only {1:F2} DIP between "
                + "the {2:F2} DIP hit circles of two vertically adjacent controls — below the "
                + "{3:F2} DIP the colour-swatch grid leaves, which is the only adjacent-target "
                + "pitch already pinned by a geometry test. A dwell target is a centre plus a "
                + "radius, so shrinking this pitch puts a neighbour's circle within a gaze "
                + "drift of the one the user is aiming at. Raise "
                + "SettingsWindow.LabelledRowMinHeight; do not lower this floor. If this failed "
                + "with the constant untouched, row assembly has left CreateControl and the "
                + "measured height no longer describes what renders.",
                measured, clearance, 2 * SettingsWindow.ControlBtnRadius, swatchClearance))
            .That(clearance).IsGreaterOrEqualThan(swatchClearance);
    }

    /// <summary>
    /// The control sits on the row's centre line, which is the point of putting the name and the
    /// control on one line at all.
    ///
    /// <para>
    /// This is what the 24 DIP bottom margin on a heading-less composite broke. That margin is
    /// the row rhythm a SELF-labelled composite carries under its heading; left unconditional it
    /// became 24 DIP of empty space inside the control column, so a 100 DIP control centred in a
    /// 150 DIP row was drawn from y=13 to y=113 with its centre at 63 while the row's centre is
    /// 75 — and DwellToggle, whose headless form never had the margin, sat 12 DIP lower on the
    /// same page. Measured on a REAL dwell child's arranged position, because that is the thing
    /// the user aims at; the hosting panel's own centre would be on the row's centre line either
    /// way and would prove nothing.
    /// </para>
    /// </summary>
    [StaFact]
    public void ALabelledRow_CentresItsControlOnTheRowsCentreLine()
    {
        var host = Host(WideColumnWidth);
        var built = SettingsWindow.CreateControl(
            Slider(), new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
            WideColumnWidth);
        var composite = (IDwellComposite)built;
        composite.Attach(host);
        LayOut(host);

        var row = (FrameworkElement)host.Children[0];
        var button = composite.Children[0].Root;
        var top = button.TransformToAncestor(row).Transform(new Point(0, 0)).Y;
        var buttonCentre = top + (button.ActualHeight / 2);
        var rowCentre = row.ActualHeight / 2;

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "A control in a labelled row is centred at y={0:F2} in a {1:F2} DIP row whose "
                + "centre is {2:F2} — off by {3:F2} DIP from the name it is supposed to sit "
                + "beside. The usual cause is a heading-less composite still carrying the 24 DIP "
                + "bottom margin that belongs to its self-labelled form: that margin is empty "
                + "space inside the control column, and it also puts sliders and combo boxes out "
                + "of line with the toggles on the same page, which never had it.",
                buttonCentre, row.ActualHeight, rowCentre,
                System.Math.Abs(buttonCentre - rowCentre)))
            .That(System.Math.Abs(buttonCentre - rowCentre)).IsStrictlyLessThan(0.51);
    }

    /// <summary>
    /// On the narrowest supported column the row STACKS. It has to: the slider's stepper row
    /// measures 404 DIP there, and being a composite it is deliberately not capped by
    /// <c>ConstrainToContentWidth</c> while its horizontal StackPanel clips rather than wraps —
    /// so a two-column row forced in here would draw stepper buttons past the card's edge with
    /// their hit circles still registered at the same centres.
    /// </summary>
    [StaFact]
    public void ANarrowColumn_StacksTheRowInsteadOfClippingIt()
    {
        var host = Host(NarrowColumnWidth);
        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            Slider(), new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
            NarrowColumnWidth);
        composite.Attach(host);
        LayOut(host);
        host.UpdateLayout();

        // Built here rather than through AttachRow because the arranged assertion below needs the
        // composite's dwell children, not just the row root. AttachRow's one-child check is
        // repeated so nothing is lost by not going through it.
        Check.WithCustomMessage(
                "A labelled row must attach exactly ONE element to the content panel. Two means "
                + "the name is stacked on the control as a separate child again, which is the "
                + "layout this row type replaced.")
            .That(host.Children.Count).IsEqualTo(1);

        var root = (FrameworkElement)host.Children[0];

        // The stacked form is a two-ROW Grid, not the StackPanel it used to be, and the shape is
        // what identifies it rather than the type: both forms are now Grids. A StackPanel could
        // only give the name and the control ONE shared box, and a 100 DIP control under a 43.92
        // DIP name cannot be centred at 75 inside a 150 DIP box — it sat 93.92 down the row with
        // 6.08 beneath it. Two rows, the second carrying the control's own pitch, is the fix; see
        // SettingsRowPitchTests.AStackedRow_CentresItsControlInASlotDeepEnoughForHalfTheFloor.
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "On a {0:F0} DIP column the row attached a {1}, not the stacked form's Grid. Below "
                + "SettingsWindow.TwoColumnMinimumWidth ({2:F0}) there is no room for a name "
                + "column beside a 404 DIP slider row, and the row must stack rather than clip.",
                NarrowColumnWidth, root.GetType().Name, SettingsWindow.TwoColumnMinimumWidth))
            .That(root).IsInstanceOf<Grid>();

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "On a {0:F0} DIP column the row's Grid has {1} row definitions and {2} column "
                + "definitions, which is not the STACKED shape (two rows — the name, then the "
                + "control's own pitch slot — and no columns). Two columns would mean it laid the "
                + "name BESIDE a slider stepper row that clips rather than wraps, drawing its "
                + "plus button off the card with the hit circle still registered there.",
                NarrowColumnWidth, ((Grid)root).RowDefinitions.Count,
                ((Grid)root).ColumnDefinitions.Count))
            .That((((Grid)root).RowDefinitions.Count, ((Grid)root).ColumnDefinitions.Count))
            .IsEqualTo((2, 0));

        // The ARRANGED right edge of the row's widest dwell child, not root.DesiredSize.Width.
        //
        // This assertion used to read `root.DesiredSize.Width <= NarrowColumnWidth` on a tree
        // measured against Size(416, infinity) seventeen lines above, and WPF CLAMPS DesiredSize
        // to the measure constraint — so it could not fail. It described a real risk (a row
        // clipping while its hit circles stay registered over blank card) and then measured the
        // one quantity that provably cannot detect it. The same trap is documented eighty lines
        // below, on NoDwellChildIsDrawnOutsideItsRow, whose own commit empirically measured a
        // 536.00 arranged edge behind a tidy 416.00 reported DesiredSize.
        //
        // An arranged edge has no such ceiling: a child pushed past the column reports where it
        // really is. Measured on a real dwell child because the circle is registered about that
        // child's centre — the row Panel's own edge is bounded by the host either way and would
        // prove nothing.
        var widest = 0.0;
        var widestIndex = -1;
        for (var i = 0; i < composite.Children.Count; i++)
        {
            var child = composite.Children[i].Root;
            var right = child.TransformToAncestor(host).Transform(new Point(0, 0)).X
                + child.ActualWidth;
            if (right > widest) (widest, widestIndex) = (right, i);
        }

        Check.WithCustomMessage(
                "The stacked slider row exposes no dwell children, so there is nothing whose "
                + "arranged edge could be measured and this guard is vacuous.")
            .That(composite.Children.Count).IsStrictlyGreaterThan(0);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The stacked row draws dwell child {0} out to {1:F2} DIP against a {2:F0} DIP "
                + "column, so it overruns it. Nothing caps a composite, and DwellSlider's stepper "
                + "row clips rather than wraps — the plus button is drawn off the card with its "
                + "hit circle still registered at the same centre, which is a live gaze target "
                + "over pixels the user cannot see. Note this is the ARRANGED edge: "
                + "root.DesiredSize.Width is clamped to the measure constraint and would report a "
                + "tidy {2:F2} however far the child ran.",
                widestIndex, widest, NarrowColumnWidth))
            .That(widest).IsLessOrEqualThan(NarrowColumnWidth + 0.01);
    }

    /// <summary>
    /// And on the column this application is actually used on, the two-column form is what
    /// renders — so it is not dead code that only a test ever sees.
    /// </summary>
    [StaFact]
    public void TheRealContentColumn_IsWideEnoughForTheTwoColumnForm()
    {
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The {0:F0} DIP content column of a 1920x1080 display is now below the {1:F0} DIP "
                + "two-column threshold, so every settings row in real use stacks and the "
                + "two-column form ships to nobody. Either the threshold grew or the window "
                + "shrank.",
                WideColumnWidth, SettingsWindow.TwoColumnMinimumWidth))
            .That(WideColumnWidth).IsGreaterOrEqualThan(SettingsWindow.TwoColumnMinimumWidth);

        var host = Host(WideColumnWidth);
        var root = AttachRow(Slider(), host);

        Check.WithCustomMessage(
                $"On the real {WideColumnWidth} DIP content column the row attached a "
                + $"{root.GetType().Name} rather than the two-column Grid.")
            .That(root).IsInstanceOf<Grid>();

        // The SHAPE, because the type no longer separates the two forms: the stacked row is a Grid
        // too now (two rows, no columns — the name above the control's own pitch slot). Two
        // COLUMNS and no row definitions is what makes this the side-by-side form, and it is the
        // form where the row itself is exactly one pitch tall.
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "On the real {0:F0} DIP content column the row's Grid has {1} row definitions and "
                + "{2} column definitions, which is not the TWO-COLUMN shape (no rows, two "
                + "columns — the name column beside the control column). Two row definitions "
                + "would mean it fell back to the stacked form on a column wide enough for both "
                + "halves.",
                WideColumnWidth, ((Grid)root).RowDefinitions.Count,
                ((Grid)root).ColumnDefinitions.Count))
            .That((((Grid)root).RowDefinitions.Count, ((Grid)root).ColumnDefinitions.Count))
            .IsEqualTo((0, 2));
    }

    /// <summary>
    /// The gaze-rate contract. <c>SettingsWindow.UpdateAllTargetPositions</c> reads
    /// <c>IDwellComposite.Children</c> once per composite per gaze frame, so a fresh collection
    /// per get allocates at gaze rate and a Gen0 collection suspends the gaze thread. The row
    /// forwards the inner composite's already-cached list, and forwards ALL of it: the row itself
    /// registers no target, so anything missing here is a control the frame loop stops moving.
    ///
    /// <para>
    /// <c>DwellCompositeChildrenStabilityTests</c> in EyeCtrl.Controls.Tests is the census that
    /// demands this coverage exist, and names this test. It cannot host the case itself: that
    /// suite does not reference EyeCtrl.App, and <c>LabelledSettingsRow</c> is private.
    /// </para>
    /// </summary>
    [StaFact]
    public void ALabelledRow_ForwardsItsInnerCompositesCachedChildren()
    {
        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            Slider(), new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
            WideColumnWidth);

        var first = composite.Children;
        var second = composite.Children;

        Check.WithCustomMessage(
                "A labelled row handed out a different Children object on the second get, so it "
                + "allocates once per row per gaze frame in "
                + "SettingsWindow.UpdateAllTargetPositions. Forward the inner composite's cached "
                + "list; never build one per get.")
            .That(ReferenceEquals(first, second)).IsTrue();

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "A labelled slider row exposes {0} dwell children, not the slider's own 2 (minus "
                + "and plus). The row wraps the composite and registers nothing itself, so it "
                + "must forward every one of the composite's targets — a dropped child is a "
                + "button the per-frame loop stops repositioning, left registered at wherever it "
                + "last was.",
                first.Count))
            .That(first.Count).IsEqualTo(2);
    }

    /// <summary>
    /// <c>SeparatorRow</c> is the second <see cref="IDwellComposite"/> implementer outside
    /// EyeCtrl.Controls, so the same two things the labelled-row pin above holds there hold here:
    /// <see cref="IDwellComposite.Children"/> hands out ONE cached object (a readonly field, never
    /// a collection expression — <c>=&gt; []</c> would allocate once per separator per gaze frame in
    /// <c>SettingsWindow.UpdateAllTargetPositions</c>), and the list is EMPTY because a rule
    /// registers no dwell target. <c>DwellCompositeChildrenStabilityTests</c> in
    /// EyeCtrl.Controls.Tests names this test in its census.
    /// </summary>
    [StaFact]
    public void ASeparatorRow_HasCachedEmptyChildren_AndAttaches()
    {
        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            new SeparatorSpec(), new SettingsAutoPagination.NoopDwellRegistrar(),
            RadiusPhysical, DwellMs, WideColumnWidth);

        var first = composite.Children;
        var second = composite.Children;
        Check.That(first).IsEmpty();
        Check.That(ReferenceEquals(first, second)).IsTrue();

        var host = new System.Windows.Controls.Grid();
        composite.Attach(host);
        Check.That(host.Children.Count).IsEqualTo(1);
    }

    /// <summary>
    /// <c>RingPreviewRow</c> is the third <see cref="IDwellComposite"/> implementer outside
    /// EyeCtrl.Controls (the settings ring miniature), so the same two things the separator pin
    /// above holds there hold here: <see cref="IDwellComposite.Children"/> hands out ONE cached
    /// object (a readonly field, never a collection expression — <c>=&gt; []</c> would allocate
    /// once per preview per gaze frame in <c>SettingsWindow.UpdateAllTargetPositions</c>), and the
    /// list is EMPTY because the preview registers no dwell target — that emptiness is the
    /// zero-target safety claim seen from the frame loop. The composite is built through
    /// production's one construction path, so its real painting ran before this assertion.
    /// <c>DwellCompositeChildrenStabilityTests</c> in EyeCtrl.Controls.Tests names this test in
    /// its census.
    /// </summary>
    [StaFact]
    public void ARingPreviewRow_HasCachedEmptyChildren_AndAttaches()
    {
        var spec = new RingPreviewSpec(
            "am_preview",
            () => RingPlan.Create(
                new[] { InteractionAction.LeftClick, InteractionAction.Keyboard },
                new[] { InteractionAction.LeftClick },
                circleSize: 250),
            "preview, not a control",
            "runtime shows labels only while you dwell");

        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            spec, new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
            WideColumnWidth);

        var first = composite.Children;
        var second = composite.Children;
        Check.That(first).IsEmpty();
        Check.That(ReferenceEquals(first, second)).IsTrue();

        var host = new System.Windows.Controls.Grid();
        composite.Attach(host);
        Check.That(host.Children.Count).IsEqualTo(1);
    }

    /// <summary>
    /// No dwell child may be DRAWN outside the row that hosts it, at either column width.
    ///
    /// <para>
    /// This is the failure the row wrapper can most easily cause and least easily be seen to
    /// cause. Two of the four composites lay their controls out in a <c>WrapPanel</c>, which
    /// wraps only when it is measured against a finite width — and a horizontal
    /// <c>StackPanel</c> host measures its children against an INFINITE one. Measured with such
    /// a host: the colour picker's four swatches stayed on one line and the last one's right edge
    /// landed at 536.00 DIP in the 416 DIP column, and a six-option combo box's last option
    /// reached 1101.13. Each of those is a registered dwell circle centred outside the card — the
    /// user dwells on blank surface and a colour changes. Nothing else in the suite noticed,
    /// because <c>DesiredSize</c> is clamped to the measure constraint and reported a tidy 416.
    /// </para>
    /// <para>
    /// Measured on the ARRANGED position of a real dwell child, which is the thing the circle is
    /// registered about, for both a picker and a deliberately long combo box, at the narrow and
    /// the wide column.
    /// </para>
    /// </summary>
    [StaTheory]
    [InlineData(NarrowColumnWidth)]
    [InlineData(WideColumnWidth)]
    public void NoDwellChildIsDrawnOutsideItsRow(double columnWidth)
    {
        SettingsRowSpec[] specs =
        [
            new ColorPickerSpec("c", "Circle Color", () => "#FFFFFF", _ => { }, 50),
            new ComboBoxSpec("cb", "Keyboard Layout",
                ["Compact", "Standard", "Extended", "Numeric Pad", "Symbols", "Emoji"], null,
                () => "Compact", _ => { }),
            // The slider, which was missing here and is the row kind that most needs it. It is
            // the control the entire stacking rule exists for: its stepper row is a horizontal
            // StackPanel, so it CLIPS rather than wraps, and its 404 DIP measurement is what sets
            // TwoColumnMinimumWidth. Every other row kind here wraps under pressure; this one is
            // the one that can silently draw a stepper button off the card with its hit circle
            // still registered — and it was the one row kind whose arranged right edge nothing
            // checked.
            Slider(),
        ];

        foreach (var spec in specs)
        {
            var host = Host(columnWidth);
            var composite = (IDwellComposite)SettingsWindow.CreateControl(
                spec, new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
                columnWidth);
            composite.Attach(host);
            LayOut(host);
            host.UpdateLayout();

            var row = (FrameworkElement)host.Children[0];

            for (var i = 0; i < composite.Children.Count; i++)
            {
                var child = composite.Children[i].Root;
                var left = child.TransformToAncestor(host).Transform(new Point(0, 0)).X;
                var right = left + child.ActualWidth;

                Check.WithCustomMessage(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: dwell child {1} is drawn from {2:F2} to {3:F2} DIP in a {4:F2} DIP "
                        + "column (row width {5:F2}). Its dwell circle is registered about its "
                        + "own centre, so a child drawn past the card is a gaze target over "
                        + "pixels the user cannot see. The usual cause is a control host that "
                        + "measures its child against an infinite width — a horizontal StackPanel "
                        + "does; a Grid hands down the width it was given, which is what lets a "
                        + "WrapPanel wrap instead of running off the edge.",
                        spec.GetType().Name, i, left, right, columnWidth, row.ActualWidth))
                    .That(right).IsLessOrEqualThan(columnWidth + 0.01);
            }
        }
    }

    /// <summary>
    /// Button rows are still deliberately NOT two-column: their label is inside the button, so
    /// they are not wrapped in a labelled row. What they DO now share with every other row kind
    /// is the box: one uniform <see cref="SettingsWindow.LabelledRowMinHeight"/> pitch.
    ///
    /// <para>
    /// This test previously pinned 124.00 and was named
    /// <c>AButtonRow_IsUnchangedByTheLabelledRowWork</c>. Both were retired deliberately — the
    /// name had become a lie, because the labelled-row work is exactly what made the old box
    /// wrong. A button row was 100 DIP of button plus a 24 DIP bottom margin, which put its hit
    /// circle 50 DIP from the row top, while a labelled row centres its 100 DIP control in a 150
    /// DIP box and puts its circle at 75. Adjacent, the two centres were only
    /// (150-75) + 50 = 125 DIP apart: 25 DIP of clearance between two 100 DIP circles, against
    /// the 40 the colour-swatch grid leaves. Every settings tab ends with a "Reset ..."
    /// <c>ButtonSpec</c>, so the page's tightest gap sat under the last setting and over a
    /// destructive action. The reason for the new number is that clearance, not appearance.
    /// </para>
    /// <para>
    /// Untouched by the change: the button's <c>Width</c>, its left alignment, its fill, and the
    /// 100 DIP WYSIWYG floor — the drawn button is the same size, only the box around it grew.
    /// <c>SettingsAutoPaginationTests</c> pins the same 150.00 from the other side, and
    /// <c>SettingsRowPitchTests</c> measures the arranged clearance it buys.
    /// </para>
    /// </summary>
    [StaFact]
    public void AButtonRow_FillsOneUniformRowPitch()
    {
        var row = new ButtonSpec("b", "Do it", () => { }, ButtonKind.Default, Width: 300, Height: 100);

        var built = SettingsWindow.CreateControl(
            row, new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
            NarrowColumnWidth);

        Check.WithCustomMessage(
                "A ButtonSpec no longer builds a bare DwellButton. Button rows carry their label "
                + "inside the button and are deliberately not wrapped in a labelled row.")
            .That(built).IsInstanceOf<DwellButton>();

        Check.WithCustomMessage(
                "The drawn button is no longer 300 x 100 DIP. The row box grew to one uniform "
                + "pitch; the BUTTON was not meant to change size with it, and 100 is the WYSIWYG "
                + "floor — the visual has to be at least as big as the hit circle it registers.")
            .That((((DwellButton)built).Root.Width, ((DwellButton)built).Root.Height))
            .IsEqualTo((300.0, 100.0));

        // A LITERAL 150.00, deliberately not SettingsWindow.LabelledRowMinHeight: production
        // derives the button's inset from that very constant, so reading it here would let the
        // pitch and this expectation move together with nothing failing. The relationship is
        // pinned separately, in SettingsAutoPaginationTests; this is the anchor.
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "A button row measures {0:F2} DIP, not the 150.00 row pitch. Every row kind now "
                + "occupies exactly one pitch so that every adjacent pair of hit circles is the "
                + "same distance apart; a button row of any other height reintroduces the "
                + "mixed-pitch gap that left a 25 DIP clearance under the last setting of every "
                + "tab. Do not re-pin this to whatever it happens to measure — fix the box.",
                SettingsAutoPagination.MeasureRowHeight(
                    row, NarrowColumnWidth, RadiusPhysical, DwellMs)))
            .That(SettingsAutoPagination.MeasureRowHeight(
                row, NarrowColumnWidth, RadiusPhysical, DwellMs))
            .IsEqualTo(150.0);
    }
}
