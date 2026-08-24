namespace EyeCtrl.App.Tests.Settings;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The clearance between the hit circles of two ADJACENT settings rows, measured on the arranged
/// tree — the one number the existing pitch guard is structurally blind to.
///
/// <para>
/// <c>SettingsRowLayoutTests.ALabelledRow_LeavesAtLeastTheSwatchGridsClearanceBetweenHitCircles</c>
/// measures ONE row's height in isolation and subtracts one circle diameter. That proxy is exact
/// only while consecutive rows are the SAME KIND: the moment two kinds with different internal
/// control offsets meet, row height stops predicting centre distance, and the guard reports a
/// healthy 50 DIP while the page draws 25.
/// </para>
/// <para>
/// Measured, before the row box was made uniform: a labelled row followed by a <c>ButtonSpec</c>
/// row put its two dwell centres 125 DIP apart at the 1424 column (clearance 25) and 120 at 416
/// (clearance 20); a STACKED slider followed by a button, 106 (clearance 6). The cause was a pure
/// asymmetry — a labelled row centres its 100 DIP control in a 150 DIP box, so its circle sits 75
/// from the row top, while a button row was 100 DIP of button plus a 24 DIP bottom margin and put
/// its circle at 50. Every one of the eleven settings tabs ends with a "Reset ..."
/// <c>ButtonSpec</c>, so the tightest gap on the page sat directly below the last setting and
/// directly above a destructive action.
/// </para>
/// <para>
/// The floor is DERIVED from the colour-swatch grid — the only adjacent-target pitch in the
/// application already pinned by a geometry test (<c>SwatchTargetGeometryTests</c>) — rather than
/// restated as a number this file could quietly adapt to. It is the one legitimate case of taking
/// an expected value from elsewhere in production: it comes from a DIFFERENT, independently
/// pinned control.
/// </para>
/// <para>
/// Read off the ARRANGED tree via <c>TransformToAncestor</c>, never <c>DesiredSize</c>, which WPF
/// clamps to the measure constraint. <c>DwellControl.UpdatePosition</c> is no use here either: it
/// calls <c>Root.PointToScreen</c>, which throws on a detached tree, and the control SWALLOWS
/// that — a probe built on it reports silently nothing. So the geometry is computed here.
/// </para>
/// </summary>
public class SettingsRowPitchTests
{
    /// <summary>The narrowest supported content column, where labelled rows STACK.</summary>
    private const double NarrowColumnWidth = 416.0;

    /// <summary>The content column of the 1920x1080 display this application is used on.</summary>
    private const double WideColumnWidth = 1424.0;

    private const double RadiusPhysical = 50;
    private const int DwellMs = 800;

    /// <summary>
    /// The clearance the colour-swatch grid leaves between two vertically adjacent swatch circles.
    /// Derived, not restated: <c>SwatchHeight</c> plus its two vertical margins is the swatch
    /// row's pitch, less one circle diameter.
    /// </summary>
    private static double SwatchClearance =>
        DwellColorPicker.SwatchHeight + (2 * DwellColorPicker.SwatchVerticalMargin)
        - (2 * SettingsWindow.ColorSwatchRadius);

    /// <summary>
    /// A host that measures the way the real content panel does: pinned to the column's width and
    /// carrying the window's font. <c>SettingsWindow</c> is an <c>OverlayWindow</c>, whose
    /// constructor sets <c>FontFamily = Tokens.FontStandard</c> and lets it inherit down; without
    /// that the measurement runs in the system font and reports positions the application never
    /// produces.
    /// </summary>
    private static StackPanel Host(double columnWidth)
    {
        var panel = new StackPanel { Width = columnWidth };
        TextElement.SetFontFamily(panel, Tokens.FontStandard);
        return panel;
    }

    /// <summary>One row's dwell targets, as arranged vertical centres in host coordinates.</summary>
    private sealed record Row(string Kind, double Top, double Bottom);

    /// <summary>
    /// Builds a whole page of rows through production's single construction path
    /// (<c>SettingsWindow.CreateControl</c>), stacks them in one host the way <c>ContentPanel</c>
    /// does, arranges it, and reports where every dwell target's centre landed.
    /// </summary>
    /// <param name="specs">The rows, in page order.</param>
    /// <param name="columnWidth">The content column's width in DIP.</param>
    /// <returns>One entry per spec, in the same order.</returns>
    private static List<Row> Place(IReadOnlyList<SettingsRowSpec> specs, double columnWidth)
    {
        var host = Host(columnWidth);
        var built = new List<object>();

        foreach (var spec in specs)
        {
            var control = SettingsWindow.CreateControl(
                spec, new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
                columnWidth);

            switch (control)
            {
                case IDwellComposite composite: composite.Attach(host); break;
                case DwellControl single: single.Attach(host); break;
                default:
                    Check.WithCustomMessage(
                            $"A {spec.GetType().Name} built a {control.GetType().Name}, which this "
                            + "probe cannot place. Row kinds that register no dwell target have no "
                            + "pitch to measure and do not belong in these sequences.")
                        .That(false).IsTrue();
                    break;
            }

            built.Add(control);
        }

        host.Measure(new Size(columnWidth, double.PositiveInfinity));
        host.Arrange(new Rect(new Size(columnWidth, host.DesiredSize.Height)));
        host.UpdateLayout();

        var rows = new List<Row>();
        for (var i = 0; i < built.Count; i++)
        {
            List<FrameworkElement> roots = built[i] switch
            {
                IDwellComposite composite =>
                    Enumerable.Range(0, composite.Children.Count)
                        .Select(c => (FrameworkElement)composite.Children[c].Root).ToList(),
                DwellControl single => [single.Root],
                _ => [],
            };

            double Centre(FrameworkElement e) =>
                e.TransformToAncestor(host).Transform(new Point(0, 0)).Y + (e.ActualHeight / 2);

            var centres = roots.Select(Centre).ToList();
            rows.Add(new Row(specs[i].GetType().Name, centres.Min(), centres.Max()));
        }

        return rows;
    }

    private static SliderSpec Slider(string id) =>
        new(id, "Smoothing Factor", 0, 1, 0.1, 1, () => 0.5, _ => { });

    private static ColorPickerSpec Picker(string id) =>
        new(id, "Circle Color", () => "#FFFFFF", _ => { }, SettingsWindow.ColorSwatchRadius);

    private static ButtonSpec Button(string id, double height) =>
        new(id, "Reset Cursor Settings", () => { }, ButtonKind.Reset, Width: 300, Height: height);

    /// <summary>
    /// Every button height the settings pages actually declare, read out of the page sources
    /// rather than restated.
    ///
    /// <para>
    /// This enumeration is the reason the button's vertical inset is DERIVED from its height in
    /// production instead of written as a literal 25. The pages ship three heights, not one: a
    /// flat 25 per side gives the 100 DIP buttons the intended 150 DIP pitch but leaves the 90 DIP
    /// type-picker buttons at 140 and the 70 DIP Delete and Back buttons at 120 — and "qa_del"
    /// follows a toggle row directly, which a flat 25 would leave 35 DIP of clearance, below the
    /// swatch floor. A test written against Height: 100 alone would have been green through that.
    /// </para>
    /// </summary>
    public static TheoryData<double> RealButtonHeightData()
    {
        var data = new TheoryData<double>();
        foreach (var height in RealButtonHeights()) data.Add(height);
        return data;
    }

    /// <summary>
    /// Distinct <c>Height:</c> arguments in the page sources, which are exactly the
    /// <c>ButtonSpec</c> heights.
    ///
    /// <para>
    /// Matched on the bare <c>Height:</c> argument rather than on <c>ButtonSpec(</c> followed by
    /// it, and the loose pattern is the correct one here. An anchored pattern missed two of the
    /// three real heights: <c>qa_del</c> carries semicolons inside its <c>ShowModal</c> lambda
    /// between the type name and the argument, and <c>BackButton</c> is a target-typed
    /// <c>new(...)</c> that never spells "ButtonSpec" at all. Both are 70 DIP — the shortest
    /// buttons, and therefore the ones a flat inset would misplace furthest. The looseness is safe
    /// because <c>ButtonSpec</c> is the only row spec with a <c>Height</c>, which
    /// <see cref="ButtonSpec_IsStillTheOnlyRowSpecWithAHeight"/> pins rather than assumes.
    /// </para>
    /// </summary>
    private static IReadOnlyList<double> RealButtonHeights()
    {
        var dir = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "Settings", "Sources");

        var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).ToList();
        var heights = files
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"Height:\s*(\d+(?:\.\d+)?)")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)))
            .Distinct()
            .OrderBy(h => h)
            .ToList();

        Check.WithCustomMessage(
                $"No button heights were found under \"{dir}\" ({files.Count} .cs files "
                + "scanned). Either the page sources "
                + "stopped declaring button rows or this pattern went stale — a theory over an "
                + "empty set proves nothing, and the whole point of reading them is that a NEW "
                + "height gets covered automatically.")
            .That(heights).Not.IsEmpty();

        return heights;
    }

    /// <summary>
    /// The premise <see cref="RealButtonHeights"/> relies on: <c>ButtonSpec</c> is the only row
    /// spec that takes a <c>Height</c>, so every <c>Height:</c> argument in the page sources is a
    /// button's. Pinned rather than assumed — a second spec kind with a Height would quietly feed
    /// this theory numbers that are not button heights.
    /// </summary>
    [Fact]
    public void ButtonSpec_IsStillTheOnlyRowSpecWithAHeight()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "Settings", "SettingsRowSpec.cs"));

        var withHeight = Regex.Matches(source, @"record\s+(\w+)\s*\(([^)]*)\)",
                RegexOptions.Singleline)
            .Where(m => m.Groups[2].Value.Contains("Height", StringComparison.Ordinal))
            .Select(m => m.Groups[1].Value)
            .ToList();

        Check.WithCustomMessage(
                "The row specs declaring a Height are now [" + string.Join(", ", withHeight)
                + "], not just ButtonSpec. RealButtonHeights in this file scrapes every "
                + "\"Height:\" argument out of the page sources and feeds them to the pitch "
                + "theories as BUTTON heights; a second spec kind with a Height makes that scrape "
                + "wrong. Narrow the pattern, or add the new kind to these sequences properly.")
            .That(withHeight).ContainsExactly("ButtonSpec");
    }

    /// <summary>
    /// THE test. A mixed page of every row-kind adjacency, at both the content column this
    /// application is actually used on and the narrowest one it supports, for every button height
    /// the application ships.
    ///
    /// <para>
    /// The sequence is built so consecutive pairs cover all four adjacencies — labelled to button,
    /// button to labelled, button to button, and labelled to labelled — with BOTH a slider and a
    /// colour picker standing in as the labelled row.
    /// </para>
    /// <para>
    /// BOTH columns, which it could not always claim. The two-column form (wide) centres its 100
    /// DIP control in a 150 DIP box and lands its circle at 75, the same place the button's does,
    /// so every pitch there is 150. The STACKED form (narrow) used to share one 150 DIP box with
    /// the name above it, which put its circle 93.92 DIP down the row and left 6.08 beneath where
    /// 20 is needed — a 31.08 DIP clearance above a "Reset ..." button, pinned for a while as a
    /// ratchet. The control now has its own 150 DIP slot BELOW the name, so the narrow column
    /// reaches the same floor and is asserted here rather than excused.
    /// </para>
    /// </summary>
    [StaTheory]
    [MemberData(nameof(RealButtonHeightData))]
    public void AdjacentRowsOfEveryKind_LeaveAtLeastTheSwatchGridsClearance(double buttonHeight)
    {
        foreach (var columnWidth in new[] { WideColumnWidth, NarrowColumnWidth })
        foreach (var (i, upper, lower, pitch, clearance) in
                 Clearances(MixedPage(buttonHeight), columnWidth))
        {
            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "On the {0:F0} DIP content column, row {1} ({2}) and row {3} ({4}) put "
                    + "their nearest dwell centres {5:F2} DIP apart, leaving only {6:F2} DIP "
                    + "between two {7:F2} DIP hit circles — below the {8:F2} DIP the colour-swatch "
                    + "grid leaves, which is the only adjacent-target pitch already pinned by a "
                    + "geometry test. A dwell target is a centre plus a radius, so a clearance "
                    + "this small puts a neighbour's circle within one gaze drift of the control "
                    + "the user is aiming at — and on every settings tab the row below the last "
                    + "setting is a \"Reset ...\" button.\n"
                    + "This is a CROSS-KIND measurement, so no row-height guard can see it: row "
                    + "height only predicts centre distance while adjacent rows are the same kind. "
                    + "The rule that keeps it true is that every row kind leaves at least {9:F2} "
                    + "DIP — half the floor — between its control's circle and each of its own row "
                    + "edges. Button height under test: {10:F2}.",
                    columnWidth, i - 1, upper.Kind, i, lower.Kind, pitch, clearance,
                    2 * SettingsWindow.ControlBtnRadius, SwatchClearance, SwatchClearance / 2,
                    buttonHeight))
                .That(clearance).IsGreaterOrEqualThan(SwatchClearance);
        }
    }

    /// <summary>
    /// And the stronger statement the fix makes at the wide column: every consecutive pitch is the
    /// SAME 150 DIP, not merely an adequate worst case.
    ///
    /// <para>
    /// Kept separate from the floor above because the two fail for different reasons and want
    /// different advice. A page can clear 40 DIP everywhere and still be uneven, and an uneven
    /// page is exactly where the next mixed-kind regression hides — that is how a 25 DIP gap
    /// shipped through a green suite.
    /// </para>
    /// <para>
    /// Only rows whose box is one pitch by construction appear here. The colour picker is
    /// deliberately absent even at this width: it is allowed to grow when its swatches wrap, so it
    /// makes no uniformity claim. The clearance floor above is what covers it.
    /// </para>
    /// <para>
    /// 150.00 is a literal, not <c>LabelledRowMinHeight</c>: production derives the button's inset
    /// from that very constant, so reading it here would let the pitch and this expectation move
    /// together with nothing failing.
    /// </para>
    /// </summary>
    [StaTheory]
    [MemberData(nameof(RealButtonHeightData))]
    public void EveryPitchOnAMixedPage_IsTheSameOneFiftyDip(double buttonHeight)
    {
        SettingsRowSpec[] page =
        [
            Slider("s1"),
            Button("b1", buttonHeight),
            new ToggleSpec("t1", "Start Paused", () => false, _ => { }),
            Button("b2", buttonHeight),
            Slider("s2"),
        ];

        var all = Clearances(page, WideColumnWidth).ToList();

        foreach (var (i, upper, lower, pitch, _) in all)
        {
            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "On a {0:F0} DIP column the pitch between row {1} ({2}) and row {3} ({4}) is "
                    + "{5:F2} DIP, not the uniform 150.00 every row kind is supposed to occupy. "
                    + "Uniformity is the invariant, not just an adequate worst case: as soon as "
                    + "one kind's control sits somewhere other than the centre of its box, row "
                    + "height stops predicting the distance between adjacent hit circles and the "
                    + "mixed-page clearance can regress with every single-row guard still green. "
                    + "All pitches on this page: {6}. Button height under test: {7:F2}.",
                    WideColumnWidth, i - 1, upper.Kind, i, lower.Kind, pitch,
                    string.Join(", ", all.Select(c =>
                        c.Pitch.ToString("F2", CultureInfo.InvariantCulture))),
                    buttonHeight))
                .That(System.Math.Abs(pitch - 150.0)).IsStrictlyLessThan(0.51);
        }
    }

    /// <summary>
    /// The narrow column, where every labelled row STACKS — now held to the SAME floor as the
    /// wide one, over the adjacencies the stacked form is most exposed to.
    ///
    /// <para>
    /// This test used to be a ratchet named <c>OnANarrowColumn_TheStackedRowIsStillShortOfTheFloor</c>,
    /// pinning a measured 31.07 DIP shortfall so it could not get worse. The shortfall was
    /// structural: below <c>TwoColumnMinimumWidth</c> the name goes ABOVE the control, and the row
    /// gave both one shared 150 DIP box — the name block measures 43.92 at 416, so the 100 DIP
    /// control ran 43.92 to 143.92 and registered its circle 93.92 DIP down the row, leaving 6.08
    /// beneath it where 20 (half the floor) is needed. A 100 DIP control under a name cannot be
    /// centred at 75 in a 150 DIP box, so the box was the bug: the control now has its OWN
    /// <c>LabelledRowMinHeight</c> slot below the name, and the row is that much taller.
    /// </para>
    /// <para>
    /// Measured after the fix, at 416: the row is 193.92 DIP tall (43.92 of name + a 150 slot),
    /// its circle sits 118.92 from the row top and 75 from the row bottom, and the worst adjacency
    /// on this page is 45.00 DIP of clearance — the colour picker above a button, unchanged by
    /// this work because a picker whose swatches wrap sets its own height. The stacked-to-button
    /// pair that measured 31.08 is now 50.00.
    /// </para>
    /// <para>
    /// A different page from <c>MixedPage</c>, on purpose: it adds the two adjacencies the mixed
    /// page has no pair for — button to SLIDER (rather than to a picker) and slider to slider —
    /// which are the pairs that put a 100 DIP stacked control directly against a neighbour with
    /// nothing wrapping in between.
    /// </para>
    /// </summary>
    [StaTheory]
    [MemberData(nameof(RealButtonHeightData))]
    public void OnANarrowColumn_TheStackedRowAlsoLeavesTheSwatchGridsClearance(double buttonHeight)
    {
        SettingsRowSpec[] page =
        [
            Slider("s1"),                   // stacked  -> button
            Button("b1", buttonHeight),     // button   -> stacked
            Slider("s2"),                   // stacked  -> stacked
            Slider("s3"),
            Picker("p1"),                   // picker   -> button
            Button("b2", buttonHeight),     // button   -> button
            Button("b3", buttonHeight),
        ];

        var all = Clearances(page, NarrowColumnWidth).ToList();

        foreach (var (i, upper, lower, pitch, clearance) in all)
        {
            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "On the {0:F0} DIP narrowest supported column, row {1} ({2}) and row {3} ({4}) "
                    + "leave {5:F2} DIP between their hit circles (pitch {6:F2}), below the "
                    + "{7:F2} DIP the colour-swatch grid leaves — the only adjacent-target pitch "
                    + "already pinned by a geometry test. Here every labelled row STACKS, and the "
                    + "rule that makes the floor reachable is that the control gets its OWN "
                    + "LabelledRowMinHeight slot BELOW the name, so its circle sits {8:F2} DIP "
                    + "from each edge of that slot however tall the name is. Sharing ONE such box "
                    + "with the name cannot work: the name takes 43.92 DIP at this width, so the "
                    + "control's circle landed at 93.92 with 6.08 of row beneath it and this same "
                    + "page measured 31.08. Do NOT re-pin this to whatever it now measures — the "
                    + "row height has to follow the name's height, because the name wraps. "
                    + "Clearances on this page: {9}. Button height: {10:F2}.",
                    NarrowColumnWidth, i - 1, upper.Kind, i, lower.Kind, clearance, pitch,
                    SwatchClearance, SwatchClearance / 2,
                    string.Join(", ", all.Select(c =>
                        c.Clearance.ToString("F2", CultureInfo.InvariantCulture))),
                    buttonHeight))
                .That(clearance).IsGreaterOrEqualThan(SwatchClearance);
        }
    }

    /// <summary>
    /// WHY the stacked form reaches that floor: the control is centred in a slot of its own, and
    /// that slot is deep enough for half the floor above and below the circle.
    ///
    /// <para>
    /// This is the local rule the cross-kind clearances are made of, and it is asserted on the
    /// LIVE row rather than restated: the slot is the row's second <c>RowDefinition</c>, whose
    /// arranged height and the control's arranged centre are both read back off the tree. Two
    /// separate claims, because they fail for different reasons — align the control host to the
    /// slot's TOP and the whole remainder falls below the circle (the centring claim); shrink the
    /// slot's MinHeight and a centred control no longer has half the floor at each edge (the depth
    /// claim). Measured: aligning Top moves the circle from 118.92 to 93.92 DIP down the row.
    /// </para>
    /// <para>
    /// Asserted on arranged geometry rather than on the host's <c>VerticalAlignment</c>, and that
    /// is deliberate. DELETING <c>VerticalAlignment.Center</c> from the control host changes
    /// nothing measurable — the composites plate their control at an explicit Height, and WPF
    /// centres a Stretch-aligned child smaller than its slot — so a property assertion would pin
    /// something the layout does not actually depend on, while missing the ways the centring can
    /// really be lost.
    /// </para>
    /// <para>
    /// The depth is DERIVED from the swatch grid and the hit radius, not from
    /// <c>LabelledRowMinHeight</c>: a slot must hold one whole circle plus the floor's worth of
    /// separation, half at each edge, or two adjacent centred controls cannot be a floor apart.
    /// Reading the production constant here would let the pitch and this expectation move
    /// together with nothing failing.
    /// </para>
    /// </summary>
    [StaFact]
    public void AStackedRow_CentresItsControlInASlotDeepEnoughForHalfTheFloor()
    {
        var host = Host(NarrowColumnWidth);
        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            Slider("s1"), new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
            NarrowColumnWidth);
        composite.Attach(host);

        host.Measure(new Size(NarrowColumnWidth, double.PositiveInfinity));
        host.Arrange(new Rect(new Size(NarrowColumnWidth, host.DesiredSize.Height)));
        host.UpdateLayout();

        var root = (Grid)host.Children[0];

        Check.WithCustomMessage(
                "The stacked row's root is no longer a two-row Grid (name row, control slot), so "
                + "there is no slot whose depth could be measured and this guard is vacuous. The "
                + "shared-box StackPanel this replaced is exactly what left the control 6.08 DIP "
                + "from the row's bottom edge.")
            .That(root.RowDefinitions.Count).IsEqualTo(2);

        var nameRowHeight = root.RowDefinitions[0].ActualHeight;
        var slotHeight = root.RowDefinitions[1].ActualHeight;
        var control = composite.Children[0].Root;
        var centre = control.TransformToAncestor(root).Transform(new Point(0, 0)).Y
            + (control.ActualHeight / 2);
        var minimumSlot = (2 * SettingsWindow.ControlBtnRadius) + SwatchClearance;

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "A stacked row's control slot is {0:F2} DIP deep, but a {1:F2} DIP hit circle "
                + "centred in it needs {2:F2} — one diameter plus the {3:F2} DIP the colour-swatch "
                + "grid leaves, half of it at each edge. Any less and two adjacent centred "
                + "controls are closer than that floor however tidy the row looks. The row here "
                + "measured {4:F2} DIP tall in total, {5:F2} of it the name.",
                slotHeight, 2 * SettingsWindow.ControlBtnRadius, minimumSlot, SwatchClearance,
                root.ActualHeight, nameRowHeight))
            .That(slotHeight).IsGreaterOrEqualThan(minimumSlot);

        var expectedCentre = nameRowHeight + (slotHeight / 2);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "A stacked row's control has its dwell centre {0:F2} DIP down the row, not "
                + "{1:F2} — the middle of its own {2:F2} DIP slot, which starts under the {3:F2} "
                + "DIP name. It is therefore {4:F2} DIP off centre, so the clearance above and "
                + "below its circle differ by twice that, and the row's pitch stops being the "
                + "distance between adjacent hit circles. The usual cause is the control host "
                + "being aligned to the slot's TOP or BOTTOM rather than its centre — note that "
                + "merely dropping VerticalAlignment.Center is inert, because the inner plate has "
                + "an explicit Height and WPF centres a Stretch-aligned child in a larger slot.",
                centre, expectedCentre, slotHeight, nameRowHeight,
                System.Math.Abs(centre - expectedCentre)))
            .That(System.Math.Abs(centre - expectedCentre)).IsStrictlyLessThan(0.51);
    }

    /// <summary>
    /// The page whose consecutive pairs cover all four row-kind adjacencies, with both a slider
    /// and a colour picker standing in as the labelled row.
    /// </summary>
    /// <param name="buttonHeight">The button height under test.</param>
    /// <returns>The row specs, in page order.</returns>
    private static SettingsRowSpec[] MixedPage(double buttonHeight) =>
    [
        Slider("s1"),                   // labelled -> button
        Button("b1", buttonHeight),     // button   -> labelled
        Picker("p1"),                   // labelled -> button
        Button("b2", buttonHeight),     // button   -> button
        Button("b3", buttonHeight),     // button   -> labelled
        Picker("p2"),                   // labelled -> labelled
        Slider("s2"),
    ];

    /// <summary>
    /// Every adjacent pair's pitch and clearance, taking the LOWEST circle of the upper row and
    /// the HIGHEST of the lower row — the tightest pair across the boundary. For a colour picker
    /// whose swatches have wrapped onto a second line those are not the row's single centre, and
    /// that difference is the whole risk.
    /// </summary>
    /// <param name="specs">The page's rows, in order.</param>
    /// <param name="columnWidth">The content column's width in DIP.</param>
    /// <returns>One tuple per adjacent pair.</returns>
    private static IEnumerable<(int Index, Row Upper, Row Lower, double Pitch, double Clearance)>
        Clearances(IReadOnlyList<SettingsRowSpec> specs, double columnWidth)
    {
        var rows = Place(specs, columnWidth);
        for (var i = 1; i < rows.Count; i++)
        {
            var pitch = rows[i].Top - rows[i - 1].Bottom;
            yield return (i, rows[i - 1], rows[i], pitch,
                pitch - (2 * SettingsWindow.ControlBtnRadius));
        }
    }
}
