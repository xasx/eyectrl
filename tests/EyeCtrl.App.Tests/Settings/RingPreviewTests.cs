namespace EyeCtrl.App.Tests.Settings;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The ring preview's gates, all headless: the arranged-tree height parity (G4), the zero-target
/// render-path registrar (G5), the chunker omit pins (G7) and append-only pagination (G8).
///
/// <para>
/// The numbers are the production display's: a 1920x1080 screen gives a 1424 DIP content column
/// and a 688 DIP content cell; 494 is the 1080p/125% budget the scale-to-fit pins; 256 is the
/// 800x600 narrow display where the preview must be absent. At the default CircleSize (250) the
/// true painting is 670 DIP square, so 688 fits unscaled, 494 scales to 494/670, and 256 falls
/// below the legibility floor.
/// </para>
/// </summary>
public class RingPreviewTests
{
    private const double ColumnWidth = 1424.0;
    private const double ReferenceBudget = 688.0;
    private const double ScaledBudget = 494.0;
    private const double NarrowBudget = 256.0;
    private const double RadiusPhysical = SettingsWindow.ControlBtnRadius;
    private const int DwellMs = 800;

    /// <summary>The default CircleSize 250 painting's true square: (125 + 70 + 60 + 70) * 2 + 20.</summary>
    private const double TrueWindowSize = 670.0;

    /// <summary>
    /// The Action Menu tab's rows, built through its source over the headless context — the same
    /// enumeration the render path flattens.
    /// </summary>
    private static List<SettingsRowSpec> ActionMenuRows(EyeCtrlSettings? settings = null) =>
        new ActionMenuPageSource(TestSettingsPageContext.Build(
                settings: settings, viewModel: QaTestSupport.MakeViewModel()))
            .Build().SelectMany(p => p.Rows).ToList();

    private static double Measure(SettingsRowSpec row) =>
        SettingsAutoPagination.MeasureRowHeight(row, ColumnWidth, RadiusPhysical, DwellMs);

    /// <summary>Places one row the way ContentPanel does, arranges it, and returns the host.</summary>
    private static StackPanel Place(SettingsRowSpec row)
    {
        var host = new StackPanel { Width = ColumnWidth };
        TextElement.SetFontFamily(host, Tokens.FontStandard);
        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            row, new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
            ColumnWidth);
        composite.Attach(host);
        host.Measure(new Size(ColumnWidth, double.PositiveInfinity));
        host.Arrange(new Rect(new Size(ColumnWidth, host.DesiredSize.Height)));
        host.UpdateLayout();
        return host;
    }

    /// <summary>Every painted button face under <paramref name="root"/>: the true-diameter ellipses.</summary>
    private static List<Ellipse> PaintedFaces(DependencyObject root) =>
        Descendants(root)
            .OfType<Ellipse>()
            .Where(e => e.Width == ActionMenuGeometry.ButtonRadius * 2)
            .ToList();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    // ────── G7: the omit pins ──────

    /// <summary>
    /// The preview is present exactly where the scaled ring fits above the legibility floor, and
    /// the scalar is <see cref="RingPlan.ScaleFor"/>'s — one value for the transform stamp and
    /// the presence decision. At 688 the default painting is unscaled (670 &lt; 688); at 494 it is
    /// scaled to 494/670; at 256 the scale (256/670) is below the 36-DIP-radius floor, so the
    /// preview — and its jump row, which would land on a page with no preview — is omitted.
    /// </summary>
    [StaTheory]
    [InlineData(ReferenceBudget, true, 1.0)]
    [InlineData(ScaledBudget, true, ScaledBudget / TrueWindowSize)]
    [InlineData(NarrowBudget, false, 0.0)]
    public void ThePreview_IsPresentAndScaledExactlyWhereItFits(
        double budget, bool present, double expectedScale)
    {
        var rows = ActionMenuRows();
        var fitted = SettingsAutoPagination.FitRingPreview(rows, ColumnWidth, budget);
        var ring = fitted.OfType<RingPreviewSpec>().SingleOrDefault();

        if (!present)
        {
            Check.WithCustomMessage(
                    $"the preview must be omitted at a {budget} DIP budget — a ring scaled below "
                    + "the legibility floor teaches a shape the eye cannot use, and absent beats "
                    + "approximate.")
                 .That(ring).IsNull();
            Check.WithCustomMessage(
                    "the jump row survived an omitted preview: it would land on a page with no "
                    + "preview, a dead affordance.")
                 .That(fitted.OfType<RingJumpSpec>()).IsEmpty();
            Check.That(fitted).HasSize(rows.Count - 2);
            return;
        }

        Check.That(ring).IsNotNull();
        Check.WithCustomMessage($"the stamped scale at {budget} DIP")
             .That(ring!.Scale).IsCloseTo(expectedScale, 1e-9);
        Check.That(fitted.OfType<RingJumpSpec>()).HasSize(1);

        // The production chunker then puts the stamped ring on the TRAILING page.
        var pages = SettingsAutoPagination.ChunkByHeight(fitted, Measure, budget);
        Check.That(pages[^1].Rows).Contains(ring);
        Check.That(pages.SelectMany(p => p.Rows)).Contains(ring);
    }

    /// <summary>
    /// The floor itself, pinned so a later "tidy the constant" edit cannot silently change which
    /// displays keep the preview: the smallest legible scale is the 36 DIP floor over the true
    /// 70 DIP radius.
    /// </summary>
    [Fact]
    public void TheLegibilityFloor_IsTheThirtySixDipRadius()
    {
        Check.That(RingPlan.MinimumLegibleScale)
             .IsCloseTo(RingPlan.LegibilityFloorButtonRadiusDip / LayoutConstants.ActionMenuButtonRadius, 1e-12);
        Check.That(RingPlan.MinimumLegibleScale).IsCloseTo(36.0 / 70.0, 1e-12);
    }

    /// <summary>
    /// The preview follows the LIVE CircleSize (the Cursor tab's slider), not a pinned radius:
    /// the true square is CircleSize + 420, so on the 688 DIP reference budget the painting is
    /// unscaled up to a 268 DIP circle and scaled above it — the acceptance's "unscaled ≤ ~268,
    /// scaled above" line, measured rather than restated.
    /// </summary>
    [StaTheory]
    [InlineData(250.0, 1.0)]              // true square 670, inside the budget
    [InlineData(268.0, 1.0)]              // true square 688, exactly the budget
    [InlineData(300.0, ReferenceBudget / 720.0)]  // true square 720, scaled to fit
    public void ThePreview_FollowsTheLiveCircleSize_UnscaledUpToTheBudget(
        double circleSize, double expectedScale)
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.CircleSize = circleSize;

        var ring = SettingsAutoPagination.FitRingPreview(ActionMenuRows(settings), ColumnWidth, ReferenceBudget)
            .OfType<RingPreviewSpec>().Single();

        Check.WithCustomMessage("the plan read a pinned radius instead of the live CircleSize")
             .That(ring.Plan().CircleSize).IsEqualTo(circleSize);
        Check.That(ring.Scale).IsCloseTo(expectedScale, 1e-9);
    }

    // ────── G4: arranged-tree height parity ──────

    /// <summary>
    /// The row reserves exactly the scaled square: the chunker's measured height equals the
    /// arranged height equals <c>WindowSize * scale</c>, and every painted button's TRANSFORMED
    /// box sits inside the content cell. The scaled case (494) is the one the
    /// <c>LayoutTransform</c> exists for; a <c>Create(s × radius)</c> painting or a
    /// <c>RenderTransform</c> that left a phantom 670 DIP slot would fail the height equality.
    /// </summary>
    [StaTheory]
    [InlineData(ReferenceBudget)]
    [InlineData(ScaledBudget)]
    public void ThePreviewRow_ReservesExactlyTheScaledSquare_AndKeepsTheRingInsideTheCell(double budget)
    {
        var rows = ActionMenuRows();
        var ring = SettingsAutoPagination.FitRingPreview(rows, ColumnWidth, budget)
            .OfType<RingPreviewSpec>().Single();
        var plan = ring.Plan();
        var expectedSide = plan.WindowSize * ring.Scale;

        // The chunker's reserved number IS the measured number — same production path.
        Check.WithCustomMessage($"the chunker's reserved height at {budget} DIP")
             .That(Measure(ring)).IsCloseTo(expectedSide, 1e-9);

        var host = Place(ring);

        Check.WithCustomMessage(
                $"the arranged row at {budget} DIP is not the square the chunker reserved — a "
                + "canvas that reports 0, a RenderTransform that keeps the true slot, or a "
                + "scaled Create would each land here.")
             .That(host.ActualHeight).IsCloseTo(expectedSide, 0.01);

        var layer = plan.BuildLayer(RingLayer.Primary, showBack: false);
        var faces = PaintedFaces(host);
        Check.WithCustomMessage("the painted button count no longer matches the layer's slots")
             .That(faces).HasSize(layer.Slots.Count);

        foreach (var face in faces)
        {
            var box = face.TransformToAncestor(host)
                .TransformBounds(new Rect(0, 0, face.Width, face.Height));
            var message = $"a painted button escapes the content cell at {budget} DIP: "
                          + $"x {box.Left:F2}..{box.Right:F2}, y {box.Top:F2}..{box.Bottom:F2}";
            Check.WithCustomMessage(message).That(box.Left).IsGreaterOrEqualThan(-0.01);
            Check.WithCustomMessage(message).That(box.Right).IsLessOrEqualThan(ColumnWidth + 0.01);
            Check.WithCustomMessage(message).That(box.Top).IsGreaterOrEqualThan(-0.01);
            Check.WithCustomMessage(message).That(box.Bottom).IsLessOrEqualThan(budget + 0.01);
        }
    }

    /// <summary>
    /// The honesty captions ship on the preview row itself (there is no room for a caption row
    /// under a painting sized to fill the height budget), with the default copy.
    /// </summary>
    [StaFact]
    public void ThePreviewRow_CarriesTheHonestyCaptions()
    {
        var ring = SettingsAutoPagination.FitRingPreview(ActionMenuRows(), ColumnWidth, ReferenceBudget)
            .OfType<RingPreviewSpec>().Single();

        var host = Place(ring);
        var texts = Descendants(host).OfType<TextBlock>().Select(t => t.Text).ToList();

        Check.That(texts).Contains(ActionMenuPageSource.RingPreviewCaptionNotAControl);
        Check.That(texts).Contains(ActionMenuPageSource.RingPreviewCaptionLabels);
    }

    // ────── G8: append-only ──────

    /// <summary>
    /// Adding the ring spec cannot move a page holding existing rows. The without-ring chunking
    /// and the production (fit + chunk) output are compared page by page: every without-ring
    /// page's rows must be a PREFIX of the with-ring page at the same index — the ring is either
    /// appended to that page or starts the next one, never inserted before an existing row. The
    /// jump row is present on both sides (it is not the ring spec), so the comparison isolates
    /// exactly what the gate protects.
    /// </summary>
    [StaFact]
    public void AddingTheRingSpec_CannotMoveAPageHoldingExistingRows()
    {
        var rows = ActionMenuRows();
        var withoutRing = rows.Where(r => r is not RingPreviewSpec).ToList();
        var withRing = SettingsAutoPagination.FitRingPreview(rows, ColumnWidth, ReferenceBudget);

        var pagesWithout = SettingsAutoPagination.ChunkByHeight(withoutRing, Measure, ReferenceBudget);
        var pagesWith = SettingsAutoPagination.ChunkByHeight(withRing, Measure, ReferenceBudget);

        Check.That(pagesWithout).Not.IsEmpty();
        Check.That(pagesWith.Count).IsGreaterOrEqualThan(pagesWithout.Count);
        Check.That(pagesWith.SelectMany(p => p.Rows)).Contains(withRing.OfType<RingPreviewSpec>().Single());

        for (var i = 0; i < pagesWithout.Count; i++)
        {
            Check.WithCustomMessage(
                    $"page {i + 1} of the with-ring chunking no longer starts with the "
                    + "without-ring page's rows. The preview is appended at the END of the row "
                    + "list; anything that moves an existing row's page (or its position within "
                    + "one) is the #113 class of regression this gate exists for.")
                 .That(pagesWith[i].Rows.Take(pagesWithout[i].Rows.Count))
                 .ContainsExactly(pagesWithout[i].Rows);
        }
    }

    // ────── The jump row's action ──────

    /// <summary>
    /// The jump row resolves its target at activation time: it lands on the LAST page, which is
    /// where the ring row always is (appended after every existing row). The page count is the
    /// shell's, set after the rows are built, so the delegate must read it when invoked.
    /// </summary>
    [Fact]
    public void TheJumpRow_NavigatesToTheTrailingPage()
    {
        var viewModel = QaTestSupport.MakeViewModel();
        viewModel.Settings.TotalPages = 7;
        viewModel.Settings.CurrentPage = 0;

        var rows = new ActionMenuPageSource(TestSettingsPageContext.Build(
                settings: new EyeCtrlSettings(), viewModel: viewModel))
            .Build().SelectMany(p => p.Rows).ToList();

        rows.OfType<RingJumpSpec>().Single().OnActivate();

        Check.That(viewModel.Settings.CurrentPage).IsEqualTo(6);
    }
}

/// <summary>
/// G5: the render-path recording registrar. The miniature's safety claim is that building the
/// whole Action Menu page registers NOTHING beyond the existing rows and the one new jump row —
/// and that claim is measured with a registrar that records, not with
/// <c>NoopDwellRegistrar</c> (which cannot be an oracle: it records nothing whatever the page
/// does). The painter's own structural half is pinned in <c>ActionMenuRingPainterTests</c>.
/// </summary>
public class RingPreviewTargetTests
{
    private const double ColumnWidth = 1424.0;
    private const double RadiusPhysical = SettingsWindow.ControlBtnRadius;
    private const int DwellMs = 800;

    private sealed class RecordingRegistrar : IDwellRegistrar
    {
        public List<string> Registered { get; } = new();

        public void Register(string id, DwellCategory category, double radiusPhysical,
            int dwellMs, Action onActivated) => Registered.Add(id);
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }

    /// <summary>
    /// Builds every row of the tab through the render path (CreateControl + Attach, exactly as
    /// MeasureRows and RenderSpec do) against a recording registrar, and returns the ids it saw.
    /// </summary>
    private static List<string> RegisteredIds(IReadOnlyList<SettingsRowSpec> rows)
    {
        var registrar = new RecordingRegistrar();
        var host = new StackPanel { Width = ColumnWidth };
        TextElement.SetFontFamily(host, Tokens.FontStandard);
        foreach (var row in rows)
        {
            switch (SettingsWindow.CreateControl(row, registrar, RadiusPhysical, DwellMs, ColumnWidth))
            {
                case DwellControl control:
                    control.Attach(host);
                    break;
                case IDwellComposite composite:
                    composite.Attach(host);
                    break;
                case DwellLabel label:
                    label.Attach(host);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled row kind: {row.GetType().Name}");
            }
        }

        return registrar.Registered.OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private static List<SettingsRowSpec> ActionMenuRows() =>
        new ActionMenuPageSource(TestSettingsPageContext.Build(
                settings: new EyeCtrlSettings(), viewModel: QaTestSupport.MakeViewModel()))
            .Build().SelectMany(p => p.Rows).ToList();

    /// <summary>
    /// Builds every row of the tab through the render path against a recording registrar, then
    /// asserts the registered ids are EXACTLY the pre-feature closed set plus the jump row. A
    /// registered id outside it (an <c>am_ring_*</c> target, a DwellControl the miniature forgot
    /// to avoid) fails here — the painter's structural half is pinned in
    /// <c>ActionMenuRingPainterTests</c>.
    /// </summary>
    [StaFact]
    public void BuildingTheWholePage_RegistersOnlyTheExistingRows_AndTheJumpRow()
    {
        var registered = RegisteredIds(ActionMenuRows());

        var expected = new HashSet<string>(StringComparer.Ordinal) { "am_reset" };
        foreach (var action in Enum.GetValues<InteractionAction>())
        {
            expected.Add("am_" + action);
            expected.Add("am_primary_" + action);
        }
        expected.Add(ActionMenuPageSource.RingJumpRowId);

        Check.WithCustomMessage(
                "The registered dwell ids on the Action Menu tab are no longer exactly the "
                + "existing rows plus the jump row. The ring miniature must register NOTHING — "
                + "it is the zero-target design — and the jump row is the only new target the "
                + "preview adds. Registered: " + string.Join(", ", registered))
             .That(registered)
             .ContainsExactly(expected.OrderBy(id => id, StringComparer.Ordinal));

        Check.WithCustomMessage(
                "A registered id carries the ring-preview prefix; the miniature is supposed to be "
                + "structurally incapable of registering, not merely untested.")
             .That(registered.Where(id => id.StartsWith("am_ring", StringComparison.Ordinal)))
             .IsEmpty();
    }

    /// <summary>
    /// The one-new-target claim from the other side: strip the jump row and the registered set is
    /// exactly what the tab registered before the preview existed — so the ring adds nothing and
    /// the jump row adds exactly itself.
    /// </summary>
    [StaFact]
    public void TheJumpRow_IsTheOnlyNewRegisteredTarget()
    {
        var rows = ActionMenuRows();
        var withJump = RegisteredIds(rows);
        var withoutJump = RegisteredIds(rows.Where(r => r is not RingJumpSpec).ToList());

        Check.That(withoutJump).Not.Contains(ActionMenuPageSource.RingJumpRowId);
        Check.That(withJump.Except(withoutJump, StringComparer.Ordinal).ToList())
             .ContainsExactly(ActionMenuPageSource.RingJumpRowId);
        Check.That(withoutJump.Except(withJump, StringComparer.Ordinal)).IsEmpty();
    }
}
