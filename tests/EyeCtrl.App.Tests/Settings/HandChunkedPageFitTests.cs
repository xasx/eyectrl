namespace EyeCtrl.App.Tests.Settings;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Xml.Linq;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Every dwell target on every settings page ANY source can show has to sit inside the content
/// cell the window actually gives it, clear of its neighbours.
///
/// <para>
/// **The hole this closes.** No height budget used to be consulted for a source whose
/// <see cref="ISettingsPageSource.PrefersAutoPagination"/> is <c>false</c>: the source emits its
/// OWN pages and <c>MaybeRepaginateByHeight</c> returns them untouched, so NOTHING measured them
/// against the budget. The content area has no <c>ScrollViewer</c> (SettingsWindow.xaml: "no
/// ScrollViewer — paginated"), so a row past the fold is not merely awkward: its hit circle is
/// registered over pixels the user cannot look at. For a user whose only input channel is gaze, a
/// registered-but-unreachable target is a setting that has silently ceased to exist.
/// </para>
/// <para>
/// That is not hypothetical. The two-column restyle made every row a uniform 150 DIP pitch and
/// added a 124 DIP page-header block, which cut the 1920x1080 content budget from 796 to 688 while
/// <c>QuickActionsPageSource</c> went on emitting six 150 DIP preset rows plus a Back row — a
/// 1050 DIP page whose last two presets were unreachable (issue #113). This is the second time a
/// layout change has pushed a hand-chunked page over.
/// </para>
///
/// <para>
/// **The sweep was widened to every source, every view, every fixture, and that is where the
/// #113 hole lived one door over.** An auto-paginated source is not measured as its source
/// <c>Build</c>s it — its rows are flattened and re-split through production's
/// <see cref="SettingsAutoPagination.ChunkByHeight"/> at the production budget, exactly what
/// <c>SettingsWindow.MaybeRepaginateByHeight</c> does with the live window's numbers, so the
/// pages measured here are the post-chunk pages the user pages through. And a multi-view source
/// is driven through <see cref="ISettingsPageSource.Views"/>, because the pages issue #113 was
/// about exist ONLY in the QuickActions picker views: the List view alone never builds a
/// <c>ChunkPresets</c> page, and a sweep that measured the landing view alone would have missed
/// the bug entirely. (The class and its guard keep their old names, "HandChunked" included,
/// because production comments cite them; the scope, not the name, is what widened.)
/// </para>
///
/// <para>
/// **This asserts reachability, not page height — do not "tighten" it into a height check.**
/// The two are different invariants and the weaker-looking one is the correct one. Measured here
/// at the 1424 DIP content column against the 688 DIP budget, the quick-action TYPE picker's page
/// is 750 DIP: it overflows the cell by 62, and the fifth circle's CENTRE (675) stays 13 DIP
/// inside the budget, so the centre a user aims at is visible; what crosses the fold is the
/// trailing margin below the final button, and the only registered pixel it touches is that
/// circle's lower edge — accepted by name below rather than silently. A "page height &lt;= budget"
/// assertion would fail that page and would have to be relaxed or suppressed, which is how a
/// guard gets deleted. The circles are what the user has to be able to aim at, so the circles are
/// what is asserted.
/// </para>
///
/// <para>
/// **Three properties are asserted, and each is where the old assertion stopped short.** (1) The
/// whole 2-D circle — centre ± <see cref="RadiusPhysical"/>, in BOTH axes — lies inside the
/// content cell; the old assertion checked centre-Y only, so a ring of targets clipped sideways,
/// or one whose lower half crossed the fold, passed. (2) Every pair of registered targets on a
/// page clears the 40 DIP swatch-grid floor; the old assertion saw no neighbours at all. (3) Dwell
/// ids are unique across a view's pages, so two pages cannot describe the same target differently.
/// </para>
///
/// <para>
/// **The budget is production's, in full.** It is derived from the same four XAML elements
/// <c>ContentAvailableHeight</c> reads (HeaderPanel's Height and Margin, ContentArea's Margin,
/// ContentCard's Padding, PaginationPanel's Margin) through
/// <see cref="SettingsContentFitTests"/>'s own readers, plus <c>PaginationReserveHeight</c>'s two
/// remaining terms — <c>SettingsWindow.PaginationButtonHeight</c> and
/// <c>PaginationButtonMargin</c>, which were made <c>internal</c> for this test exactly as
/// <c>WindowSizeRatio</c>, <c>ControlBtnRadius</c> and <c>LabelledRowMinHeight</c> already were.
/// So no term of the budget is restated here, and none is omitted: the number below IS the number
/// the window computes. (An optimistic budget that dropped the 112 DIP pagination reserve would
/// still have caught issue #113 — 825 &gt; 800 — but would have silently tolerated any page
/// overflowing by less than a pagination bar, which is one 150 DIP row's worth of near-miss.)
/// </para>
///
/// <para>
/// **Geometry comes off the ARRANGED tree** via <c>TransformToAncestor</c>, the technique
/// <see cref="SettingsRowPitchTests"/> uses, never from <c>DesiredSize</c> (WPF clamps that to the
/// measure constraint) and never via <c>DwellControl.UpdatePosition</c>, which calls
/// <c>Root.PointToScreen</c> — that throws on a detached tree and the control SWALLOWS the
/// exception, so a probe built on it reports nothing at all and passes.
/// </para>
/// <para>
/// **Nothing is shown and no input is simulated**: a detached element tree renders no window. No UI
/// category, so CI runs it.
/// </para>
/// </summary>
public class HandChunkedPageFitTests
{
    /// <summary>
    /// The display this application is used on, and the one the numbers in this file's remarks
    /// were measured at.
    ///
    /// <para>
    /// Deliberately NOT the 800x600 DIP narrow display <see cref="SettingsContentFitTests"/>
    /// checks widths against. That display's height budget is 256 DIP — one and a half rows — so
    /// NO hand-chunked page with a Back row plus even two presets can fit it, at any per-page
    /// count. Making a hand-chunked page fit an arbitrarily short display needs re-chunking
    /// against the live budget, i.e. <c>PrefersAutoPagination</c>, which QuickActions cannot have
    /// (its page boundaries carry navigation semantics — per-page back-button ids). Asserting the
    /// narrow display here would therefore be asserting something the design cannot satisfy;
    /// scoping to the real display is the honest guard, and the gap is named rather than hidden.
    /// (Auto-paginated sources DO re-chunk against that live budget and are exercised there by
    /// <c>SettingsAutoPaginationTests</c>; this file measures the reference display, where the
    /// width is roomy enough for every two-column row.)
    /// </para>
    /// <para>
    /// KNOWN GAP, recorded not solved: on a genuinely small display the QuickActions tab therefore
    /// has unreachable dwell targets that no per-page constant can fix, and this guard will not
    /// see them. It is the last part of issue #113 still open — the three targets that WERE
    /// tracked here are fixed — and it is scoped out by display size, not by any exception list.
    /// Do not attempt it by widening the assertion here, which would only make the guard
    /// unsatisfiable and invite its deletion.
    /// </para>
    /// </summary>
    private const double DisplayWidthDip = 1920;

    /// <inheritdoc cref="DisplayWidthDip"/>
    private const double DisplayHeightDip = 1080;

    private const double RadiusPhysical = SettingsWindow.ControlBtnRadius;
    private const int DwellMs = 800;

    /// <summary>
    /// Sub-pixel slack for geometry comparisons. The arranged numbers here are exact at 100% DPI
    /// for everything but intentional fractions (a wrapped swatch row's margins, a scaled plate),
    /// and a comparison against a floor one float-operation away must not read a 139.99999999 as
    /// a failure. An actual violation of any bound asserted here is DIP-scale, never sub-pixel.
    /// </summary>
    private const double Tolerance = 0.01;

    /// <summary>
    /// The content cell's height in DIP — every term of <c>SettingsWindow.ContentAvailableHeight</c>,
    /// read off the live XAML and off production's own consts.
    /// </summary>
    /// <param name="root">The parsed SettingsWindow.xaml root.</param>
    /// <returns>The height budget in DIP.</returns>
    internal static double ContentHeightBudget(XElement root) =>
        ContentHeightBudget(root, DisplayHeightDip);

    /// <inheritdoc cref="ContentHeightBudget(XElement)" />
    /// <param name="root">The parsed SettingsWindow.xaml root.</param>
    /// <param name="displayHeightDip">The primary display's height in DIP.</param>
    /// <returns>The height budget in DIP.</returns>
    internal static double ContentHeightBudget(XElement root, double displayHeightDip)
    {
        var header = SettingsContentFitTests.Margin(root, "HeaderPanel");
        var contentArea = SettingsContentFitTests.Margin(root, "ContentArea");
        var cardPadding = SettingsContentFitTests.Padding(root, "ContentCard");
        var pagination = SettingsContentFitTests.Margin(root, "PaginationPanel");

        return SettingsContentFitTests.WindowHeightFor(displayHeightDip)
            - SettingsContentFitTests.FixedHeight(root, "HeaderPanel")
            - header.Top - header.Bottom
            - contentArea.Top - contentArea.Bottom
            - cardPadding.Top - cardPadding.Bottom
            // PaginationReserveHeight, in full: the bar is reserved unconditionally.
            - SettingsWindow.PaginationButtonHeight
            - (2 * SettingsWindow.PaginationButtonMargin)
            - pagination.Top - pagination.Bottom;
    }

    /// <summary>The content column's width in DIP, the same derivation the width guards use.</summary>
    /// <param name="root">The parsed SettingsWindow.xaml root.</param>
    /// <returns>The column width in DIP.</returns>
    internal static double ContentColumnWidth(XElement root) =>
        ContentColumnWidth(root, DisplayWidthDip);

    /// <inheritdoc cref="ContentColumnWidth(XElement)" />
    /// <param name="root">The parsed SettingsWindow.xaml root.</param>
    /// <param name="displayWidthDip">The primary display's width in DIP.</param>
    /// <returns>The column width in DIP.</returns>
    internal static double ContentColumnWidth(XElement root, double displayWidthDip)
    {
        var contentArea = SettingsContentFitTests.Margin(root, "ContentArea");
        var cardPadding = SettingsContentFitTests.Padding(root, "ContentCard");
        return SettingsContentFitTests.WindowWidthFor(displayWidthDip)
            - SettingsContentFitTests.SidebarWidth(root)
            - contentArea.Left - contentArea.Right
            - cardPadding.Left - cardPadding.Right;
    }

    /// <summary>
    /// The clearance floor between two registered targets' hit circles, DERIVED from the colour
    /// swatch grid — the only adjacent-target pitch in the application already pinned by a
    /// geometry test (<c>SwatchTargetGeometryTests</c>) — rather than restated as a number this
    /// file could quietly adapt to. It is the swatch row's vertical pitch: one cell plus its two
    /// gutters, less one circle diameter.
    ///
    /// <para>
    /// Same derivation and same reason as <see cref="SettingsRowPitchTests"/>'s private
    /// <c>SwatchClearance</c>: taking an expected value from a DIFFERENT, independently pinned
    /// control beats inventing a threshold here.
    /// </para>
    /// </summary>
    private static double ClearanceFloorDip =>
        DwellColorPicker.SwatchHeight + (2 * DwellColorPicker.SwatchVerticalMargin)
        - (2 * SettingsWindow.ColorSwatchRadius);

    // ────── Source discovery ──────

    /// <summary>
    /// Every concrete <see cref="ISettingsPageSource"/> in the EyeCtrl.App assembly, found by
    /// reflection rather than listed by hand so a new tab is covered the day it is written.
    /// </summary>
    /// <returns>The implementing types, by name.</returns>
    private static IReadOnlyList<Type> AllSourceTypes() =>
        typeof(ISettingsPageSource).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(ISettingsPageSource).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Constructs one source over the headless page context, failing loudly if it cannot.
    ///
    /// <para>
    /// A source this harness cannot build must FAIL, never be skipped: silently dropping it is
    /// exactly how the guard would come to cover nothing while staying green.
    /// </para>
    /// </summary>
    /// <param name="type">The source type.</param>
    /// <param name="viewModel">The view model the source reads its live state from.</param>
    /// <param name="registry">The model registry, for sources that read one.</param>
    /// <param name="modelManagement">The download service, for sources that read one.</param>
    /// <param name="dictionary">The learned-words service, for the Keyboard tab's dictionary page.</param>
    /// <returns>The constructed source.</returns>
    private static ISettingsPageSource Construct(
        Type type, SettingsWindowViewModel viewModel,
        ModelRegistryService? registry = null, ModelManagementService? modelManagement = null,
        UserDictionaryService? dictionary = null)
    {
        var ctor = type.GetConstructor([typeof(SettingsPageContext)])
            // Sources may declare additional OPTIONAL constructor dependencies (the Adaptation page's
            // open-log callback); the harness builds those with their defaults — the guard measures
            // layout reachability, not wiring, and the tripwire below still fails loudly when neither
            // shape exists.
            ?? type.GetConstructors()
                .Where(c =>
                {
                    var ps = c.GetParameters();
                    return ps.Length > 0
                        && ps[0].ParameterType == typeof(SettingsPageContext)
                        && ps.Skip(1).All(p => p.HasDefaultValue);
                })
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault();
        Check.WithCustomMessage(
                $"{type.Name} has no (SettingsPageContext) constructor, so this guard cannot "
                + "build it — and a settings page this harness cannot build is a page nothing "
                + "measures against the height budget. Teach the harness to construct it; do NOT "
                + "filter it out.")
            .That(ctor).IsNotNull();

        var ctx = TestSettingsPageContext.Build(
            viewModel: viewModel, registry: registry, modelManagement: modelManagement,
            dictionary: dictionary);
        try
        {
            var ps = ctor!.GetParameters();
            var args = new object?[ps.Length];
            args[0] = ctx;
            for (int i = 1; i < ps.Length; i++)
                args[i] = ps[i].DefaultValue;
            return (ISettingsPageSource)ctor.Invoke(args);
        }
        catch (Exception ex)
        {
            Check.WithCustomMessage(
                    $"{type.Name}'s constructor threw {ex.GetType().Name} over the headless page "
                    + $"context: {ex.Message}. Give TestSettingsPageContext.Build the dependency "
                    + "it wants; do NOT filter this source out of the guard.")
                .That(false).IsTrue();
            throw;
        }
    }

    /// <summary>
    /// How many quick actions the "configured" fixture seeds. Five, so that both a FULL action
    /// page and a PARTIAL one get measured whatever <c>QaActionsPerPage</c> is set to: at its
    /// current 2 that is pages of 2/2/1, and at the 4 it used to be it was 4/1. Deliberately not
    /// pinned to the constant — a fixture derived from the value under test would follow it into
    /// whatever new page shape a future change picks, and stop being an independent probe of it.
    /// Each action contributes TWO rows (an enable toggle and a Delete button), which is the
    /// factor that made 4 put eight rows on a four-row page (issue #113).
    /// </summary>
    private const int SeededActionCount = 5;

    /// <summary>
    /// The states of the world a source has to be measured in.
    ///
    /// <para>
    /// One fixture is not enough, and the reason is the same "guard over an empty set" trap this
    /// file's tripwires are about, one level down. <c>QuickActionsPageSource.List</c> emits a page
    /// per group of configured actions, so over the default fixture — whose store is EMPTY — those
    /// pages do not exist and nothing measures them. A user with quick actions configured gets
    /// pages this harness would never have built. The same shape holds one tab over: Models'
    /// management view exists only while a model is downloaded, and the Keyboard tab's
    /// learned-words page only when the context carries a dictionary service with learned words.
    /// </para>
    /// </summary>
    /// <param name="type">The source type.</param>
    /// <returns>(fixture name, constructed source, owned scope) per fixture.</returns>
    private static IEnumerable<(string Fixture, ISettingsPageSource Source, IDisposable? Scope)>
        FixturesOf(Type type)
    {
        if (type == typeof(QuickActionsPageSource))
        {
            yield return ("nothing configured", Construct(type, QaTestSupport.MakeViewModel()), null);

            var configured = QaTestSupport.MakeViewModel();
            for (var i = 0; i < SeededActionCount; i++)
                configured.QuickActionsEditor.AddKeyboardShortcut("Action " + i, "Ctrl+" + i);
            yield return ($"{SeededActionCount} actions configured", Construct(type, configured), null);
            yield break;
        }

        if (type == typeof(ModelsPageSource))
        {
            // The registry is REQUIRED (Build reads DownloadedModels off it), so even the empty
            // fixture gets a real one — over a unique temp dir, as ModelTestFakes' remarks
            // explain. Both fixtures are returned with their scopes so the caller can dispose
            // them; a leaked temp dir per run is the alternative.
            var empty = ModelTestFakes.Create();
            var emptyRegistry = empty.EmptyRegistry();
            yield return ("no models downloaded",
                Construct(type, QaTestSupport.MakeViewModel(),
                    registry: emptyRegistry, modelManagement: empty.NoOpManagement(emptyRegistry)),
                empty);

            var downloaded = ModelTestFakes.Create();
            var (registry, management) = downloaded.WithDownloadedBundled();
            yield return ("one model downloaded",
                Construct(type, QaTestSupport.MakeViewModel(),
                    registry: registry, modelManagement: management),
                downloaded);
            yield break;
        }

        if (type == typeof(KeyboardPageSource))
        {
            // Both states are real: the shell supplies a dictionary service, and the page's
            // learned-words rows exist only once words have been learned. The null-service form
            // (degraded hosts, and this harness's plain context) keeps its single page.
            yield return ("no dictionary service", Construct(type, QaTestSupport.MakeViewModel()), null);

            var learned = new LearnedWordsFixture();
            yield return ("learned words",
                Construct(type, QaTestSupport.MakeViewModel(), dictionary: learned.Service), learned);
            yield break;
        }

        yield return ("nothing configured", Construct(type, QaTestSupport.MakeViewModel()), null);
    }

    /// <summary>
    /// Every page set a source can put on screen, one entry per navigable sub-view.
    ///
    /// <para>
    /// The sub-view state machine is declared by the source itself through
    /// <see cref="ISettingsPageSource.Views"/> — the seam this file's tripwires make sure stays
    /// honest. A source that declares no views is a single-view source and its one
    /// <see cref="ISettingsPageSource.Build"/> is the whole of what it can show.
    /// </para>
    /// </summary>
    /// <param name="source">The constructed source.</param>
    /// <returns>(view name, pages) per sub-view.</returns>
    private static IEnumerable<(string View, IReadOnlyList<PageSpec> Pages)> ViewsOf(
        ISettingsPageSource source)
    {
        var views = source.Views;
        if (views.Count == 0)
        {
            yield return ("(default)", source.Build());
            yield break;
        }

        foreach (var (name, enter) in views)
        {
            enter();
            yield return (name, source.Build());
        }
    }

    /// <summary>
    /// The pages production actually renders for this source: the source's own pages for a
    /// hand-chunked one, and for an auto-paginated one its rows flattened and run through
    /// production's full pagination pipeline — <see cref="SettingsAutoPagination.Repaginate"/>,
    /// which is the ring preview's fit THEN the height chunker, the same two steps
    /// <c>SettingsWindow.MaybeRepaginateByHeight</c> runs with the live window's numbers.
    ///
    /// <para>
    /// This is the door the guard used to skip: auto-paginated sources were left out of the
    /// sweep entirely, so nothing ever measured the post-chunk pages production renders. The
    /// chunker's own tests cover the chunking arithmetic; this measures the pages that come out.
    /// It calls the production method rather than restating its two steps, because a restated
    /// pipeline drifts the next time production grows one (PR #175 F2).
    /// </para>
    /// </summary>
    /// <param name="source">The source whose pages these are.</param>
    /// <param name="pages">The pages as the source built them.</param>
    /// <param name="budget">The content cell's height in DIP.</param>
    /// <param name="columnWidth">The content column's width in DIP.</param>
    /// <returns>The pages production would render, in order.</returns>
    private static IReadOnlyList<PageSpec> PagesAsRendered(
        ISettingsPageSource source, IReadOnlyList<PageSpec> pages, double budget, double columnWidth)
    {
        if (!source.PrefersAutoPagination) return pages;

        var rows = pages.SelectMany(p => p.Rows).ToList();
        return SettingsAutoPagination.Repaginate(rows, columnWidth, budget, RadiusPhysical, DwellMs);
    }

    // ────── Placement ──────

    /// <summary>One dwell target, as its arranged centre in content-panel coordinates.</summary>
    private sealed record Target(string DwellId, double CentreX, double CentreY);

    /// <summary>
    /// Builds a whole page through production's single construction path
    /// (<c>SettingsWindow.CreateControl</c>), stacks it the way <c>ContentPanel</c> does, arranges
    /// it, and reports where every registered dwell circle's centre landed.
    /// </summary>
    /// <param name="page">The page to place.</param>
    /// <param name="columnWidth">The content column's width in DIP.</param>
    /// <returns>The page's arranged height, and its dwell targets in page order.</returns>
    private static (double PageHeight, List<Target> Targets) Place(PageSpec page, double columnWidth)
    {
        // Pinned to the column's width and carrying the window's font: SettingsWindow is an
        // OverlayWindow, whose ctor sets FontFamily = Tokens.FontStandard and lets it inherit
        // down. Without that, labels measure in the system font and every position is one the
        // application never produces.
        var host = new StackPanel { Width = columnWidth };
        TextElement.SetFontFamily(host, Tokens.FontStandard);

        var built = new List<object>();
        foreach (var spec in page.Rows)
        {
            var control = SettingsWindow.CreateControl(
                spec, new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
                columnWidth);

            // Same order, and the same ConstrainToContentWidth call, as the render path and as
            // SettingsAutoPagination.MeasureRows — a control squeezed on screen must be placed
            // under the same squeeze here.
            switch (control)
            {
                case DwellControl single:
                    SettingsWindow.ConstrainToContentWidth(single.Root, columnWidth);
                    single.Attach(host);
                    break;
                case IDwellComposite composite:
                    composite.Attach(host);
                    break;
                case DwellLabel label:
                    // Registers no dwell target — it contributes height, never a circle.
                    label.Attach(host);
                    break;
                default:
                    Check.WithCustomMessage(
                            $"A {spec.GetType().Name} built a {control.GetType().Name}, which this "
                            + "probe cannot place. Teach it the new control kind — a row kind it "
                            + "drops is a row kind whose circles nothing checks.")
                        .That(false).IsTrue();
                    break;
            }

            built.Add(control);
        }

        host.Measure(new Size(columnWidth, double.PositiveInfinity));
        host.Arrange(new Rect(new Size(columnWidth, host.DesiredSize.Height)));
        host.UpdateLayout();

        var targets = new List<Target>();
        foreach (var control in built)
        {
            List<DwellControl> circles = control switch
            {
                IDwellComposite composite => composite.Children.ToList(),
                DwellControl single => [single],
                _ => [],
            };

            foreach (var circle in circles)
            {
                var box = circle.Root
                    .TransformToAncestor(host)
                    .TransformBounds(new Rect(circle.Root.RenderSize));
                targets.Add(new Target(
                    circle.DwellId, box.Left + (box.Width / 2), box.Top + (box.Height / 2)));
            }
        }

        return (host.ActualHeight, targets);
    }

    // ────── Tripwires ──────

    /// <summary>
    /// A guard over an empty set proves nothing. If reflection finds no page sources — renamed
    /// interface, moved assembly, trimmed types — this fails instead of reporting a clean sweep
    /// over zero pages.
    /// </summary>
    [Fact]
    public void Reflection_FindsThePageSources()
    {
        var types = AllSourceTypes();
        Check.WithCustomMessage(
                "Reflection over the EyeCtrl.App assembly found NO concrete ISettingsPageSource "
                + "implementation. The settings window has fifteen tabs, so this is a broken "
                + "discovery query, not an empty codebase — and every assertion in this file "
                + "would otherwise iterate nothing and pass.")
            .That(types).Not.IsEmpty();
    }

    /// <summary>
    /// The view seam has to stay honest in BOTH directions, and this is where that is checked.
    ///
    /// <para>
    /// Direction one: the two sources that really do have view state machines must declare
    /// their views through <see cref="ISettingsPageSource.Views"/>, by the exact names the gate
    /// will report — a gutted override would otherwise quietly reduce them to one page.
    /// </para>
    /// <para>
    /// Direction two, and the one that survives this file being edited: a source that grows a
    /// view state machine must declare it. The marker is a nested <c>…View</c> enum in its
    /// source file (that is the shape both real ones use) and the declaration is a
    /// <c>Views =&gt;</c> override; a file with the first and not the second fails here instead
    /// of being measured on its landing page alone. It is a scan, so it can only catch the shape
    /// it knows — the same limit the source-drift scans elsewhere accept.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheViewSeam_DeclaresEveryViewOfEverySource()
    {
        var qa = (QuickActionsPageSource)Construct(
            typeof(QuickActionsPageSource), QaTestSupport.MakeViewModel());
        Check.WithCustomMessage(
                "QuickActionsPageSource.Views no longer names its six views in enum order. The "
                + "reachability gate drives these; a shorter list means picker pages nothing "
                + "measures — and those are the pages issue #113 was about.")
            .That(qa.Views.Select(v => v.Name)).ContainsExactly(
                "List", "PickType", "PickShortcut", "PickMedia", "PickApp", "PickWindowAction");

        var models = (ModelsPageSource)Construct(
            typeof(ModelsPageSource), QaTestSupport.MakeViewModel());
        Check.WithCustomMessage(
                "ModelsPageSource.Views no longer names the list and management views. The "
                + "management view's delete rows exist only behind it; a shorter list means "
                + "nothing measures them.")
            .That(models.Views.Select(v => v.Name)).ContainsExactly("List", "Manage");

        var dir = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "Settings", "Sources");
        var viewEnums = Directory.EnumerateFiles(dir, "*.cs")
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                SourceScanner.StripComments(File.ReadAllText(f)), @"enum\s+\w*View\b"))
            .ToList();

        Check.WithCustomMessage(
                "No source file under Views/Settings/Sources declares a nested …View enum, so "
                + "the tripwire below now sweeps an empty set and would pass no matter how many "
                + "view state machines exist. If the state machines were renamed or reshaped, "
                + "repoint this scan at the new shape — do not delete it.")
            .That(viewEnums).Not.IsEmpty();

        var undeclared = viewEnums
            .Where(f => !SourceScanner.StripComments(File.ReadAllText(f)).Contains("Views =>"))
            .Select(f => Path.GetFileName(f))
            .ToList();
        Check.WithCustomMessage(
                "These source files declare a …View state machine but no `Views =>` override, so "
                + "the reachability gate would measure only their landing view. Declare every "
                + "view through ISettingsPageSource.Views (see QuickActionsPageSource for the "
                + "pattern) — a picker page nothing drives is a page nothing measures.")
            .That(undeclared).IsEmpty();
    }

    // ────── The known-unreachable ratchet: RETIRED, deliberately ──────

    // There was a KnownUnreachableTargets ratchet here — three entries, matched by id or prefix,
    // asserted as an EQUALITY in both directions so that a new unreachable target failed AND a
    // listed one that became reachable failed too. It held issue #113's three pre-existing
    // offenders: qa_add on the List config page, and the qa_del_/qa_en_ rows on the per-action
    // pages. All three are fixed at source (QuickActionsPageSource: QaActionsPerPage 4 -> 2, and
    // the config rows chunked at QaConfigRowsPerPage with qa_add leading), and it was this file's
    // own direction-2 assertion that demanded each entry's deletion as its bug was closed —
    // "the count went DOWN, that row is REACHABLE again".
    //
    // The machinery is gone with the last entry rather than left behind holding an empty array,
    // and that is still the point: an EMPTY ratchet is a place to PUT a failure, and the next
    // person to break a page could add an entry and go green. The exemption below is
    // deliberately NOT that. It is named, single-purpose, carries the measurement that earns
    // it, and is ratcheted in BOTH directions just as the old list was — an exempted pair
    // that stops needing its exemption FAILS until the entry is deleted, and an exempted target
    // that gets WORSE than its recorded measurement fails too. So an entry cannot absorb a new
    // offender, only the one geometry it names; adding another entry is a visible act in a diff
    // someone reviews.

    // ────── Named, measured exemptions ──────

    /// <summary>
    /// A target the full-circle rule rejects, exempted BY NAME with the measurement that earns
    /// it. Matched on source, view and dwell id — the narrowest key that still names one target
    /// — and the actual overflow may not exceed the recorded measurement, so the exemption
    /// cannot hide the page getting worse.
    /// </summary>
    /// <param name="Source">The source type name the target belongs to.</param>
    /// <param name="View">The view the target is shown in.</param>
    /// <param name="DwellId">The dwell id, exactly as registered.</param>
    /// <param name="MeasuredOverflowDip">How far the circle's edge crosses the budget, in DIP.</param>
    /// <param name="Reason">Why the project accepts it — the measurement, in words.</param>
    private sealed record ReachExemption(
        string Source, string View, string DwellId, double MeasuredOverflowDip, string Reason);

    /// <summary>
    /// The one page this project has already decided to accept as reachable-but-overflowing,
    /// measured here rather than assumed.
    ///
    /// <para>
    /// The quick actions TYPE picker is a hard-wired page — a Back row plus four 90 DIP type
    /// buttons — and every row is centred in a 150 DIP box, so the page is 750 DIP against the
    /// 688 DIP cell. The fifth circle's centre lands at 675, 13 DIP inside the budget; what
    /// crosses the fold is the trailing margin below the final row plus that row's own content.
    /// The 90 DIP button face is centred in its 150 DIP box, so it spans 630..720 — its lower
    /// 32 DIP is drawn below the fold — and its hit circle spans 625..725, 37 DIP below. Nothing
    /// else under the last 62 DIP of the page is drawn or registered (PR #175 F3: the earlier
    /// prose said nothing was drawn, which understated the button face). Re-chunking the tab's
    /// entry point is a UX decision this gate does not take (the class remarks above and
    /// <c>QaPresetsPerPage</c>'s carry the same position), and the trailing-slack guard beside
    /// this one names the same page for the same reason. Fixing the page rather than exempting
    /// it is the better outcome the day someone decides to spend the extra page-turn; until then
    /// the exemption carries the number.
    /// </para>
    /// </summary>
    private static readonly ReachExemption[] ReachExemptions =
    [
        new(
            Source: nameof(QuickActionsPageSource),
            View: nameof(QuickActionsPageSource.QaView.PickType),
            DwellId: "qa_type_window",
            MeasuredOverflowDip: 37.00,
            Reason: "measured 2026-09-30: type-picker page 750.00 DIP vs the 688.00 budget; "
                    + "centre 675.00 (13 inside), circle y 625.00..725.00, so its lower 37.00 "
                    + "crosses the fold — the drawn 90 DIP face (630.00..720.00) puts 32.00 below "
                    + "it too; accepted page, see the class remarks and QaPresetsPerPage"),
    ];

    /// <summary>
    /// A pair of registered targets the clearance floor rejects, exempted BY NAME with the
    /// measurement that earns it. Matched on the dwell-id prefix both targets carry (ids are
    /// <c>{prefix}_{index}</c>), and the actual clearance may not be less than the recorded
    /// measurement — a pair that gets tighter than it has ever been is a failure, not a
    /// coincidence.
    /// </summary>
    /// <param name="IdPrefixes">
    /// The colour pickers whose swatch pairs are exempt, by the id prefix their swatch ids carry:
    /// <c>c_color</c> (Cursor), <c>s_highlight</c> (Selection), <c>av_drag_color</c> (Advanced),
    /// <c>cal_color</c> (Calibration). Each prefix belongs to exactly one picker, and a pair is
    /// covered only when BOTH ids carry the same one.
    /// </param>
    /// <param name="MeasuredClearanceDip">The clearance the grid has always left, in DIP.</param>
    /// <param name="Reason">Why the project accepts it — the measurement, in words.</param>
    private sealed record ClearanceExemption(
        string[] IdPrefixes, double MeasuredClearanceDip, string Reason);

    /// <summary>
    /// EMPTY since 2026-09-30: the swatch-grid entry was retired the day the horizontal
    /// gutter widened from 8 to 10 DIP (<c>DwellColorPicker.SwatchHorizontalMargin</c>),
    /// putting adjacent centres 140 apart and clearing the floor outright — the
    /// both-direction ratchet below is what forced this deletion. Kept as an empty, named
    /// mechanism rather than removed so a future legitimate case is added as a visible,
    /// measured entry (see the pairwise failure text), never a threshold tweak.
    /// </summary>
    private static readonly ClearanceExemption[] ClearanceExemptions = [];

    // ────── The guard ──────

    /// <summary>One unreachable target, with enough context to name it in a failure.</summary>
    private sealed record Unreachable(
        string Source, string View, string Fixture, int Page, int Pages, double PageHeight,
        string DwellId, double Left, double Right, double Top, double Bottom)
    {
        /// <summary>Renders this offender as one line of a failure message.</summary>
        /// <returns>The formatted line.</returns>
        public string Describe() => string.Format(
            CultureInfo.InvariantCulture,
            "  {0}.{1} [{2}] page {3}/{4} (page height {5:F2}): \"{6}\" circle "
            + "x {7:F2}..{8:F2}, y {9:F2}..{10:F2}",
            Source, View, Fixture, Page, Pages, PageHeight, DwellId, Left, Right, Top, Bottom);
    }

    /// <summary>Two registered targets whose hit circles are closer than the floor.</summary>
    private sealed record ClearancePair(
        string Source, string View, string Fixture, int Page, int Pages,
        string FirstId, double FirstX, double FirstY,
        string SecondId, double SecondX, double SecondY,
        double Distance)
    {
        /// <summary>The empty space between the two circles, in DIP.</summary>
        public double Clearance => Distance - (2 * RadiusPhysical);

        /// <summary>Renders this offender as one line of a failure message.</summary>
        /// <returns>The formatted line.</returns>
        public string Describe() => string.Format(
            CultureInfo.InvariantCulture,
            "  {0}.{1} [{2}] page {3}/{4}: \"{5}\" ({6:F2}, {7:F2}) and \"{8}\" ({9:F2}, {10:F2}) "
            + "are {11:F2} apart — {12:F2} of clearance between two {13:F2} circles (floor {14:F2})",
            Source, View, Fixture, Page, Pages, FirstId, FirstX, FirstY, SecondId, SecondX,
            SecondY, Distance, Clearance, 2 * RadiusPhysical, ClearanceFloorDip);
    }

    /// <summary>One dwell id registered on more than one page of the same view.</summary>
    private sealed record DuplicateId(
        string Source, string View, string Fixture, string DwellId, int FirstPage, int AgainOnPage)
    {
        /// <summary>Renders this offender as one line of a failure message.</summary>
        /// <returns>The formatted line.</returns>
        public string Describe() => string.Format(
            CultureInfo.InvariantCulture,
            "  {0}.{1} [{2}]: \"{3}\" is used on page {4} and again on page {5}",
            Source, View, Fixture, DwellId, FirstPage, AgainOnPage);
    }

    /// <summary>
    /// The invariant: on every page every source emits, in every view, under every fixture, each
    /// registered dwell circle's whole 2-D extent sits inside the content cell, every pair of
    /// registered circles on a page clears the swatch-grid floor, and no view registers the same
    /// dwell id on two pages.
    ///
    /// <para>
    /// Nowhere to record a failure but the named exemption above, which carries its
    /// measurement and is ratcheted in both directions — see the retirement note for why there is
    /// no general suppression list, and why it is not one.
    /// </para>
    /// </summary>
    [StaFact]
    public void EveryHandChunkedPage_KeepsItsDwellCirclesInsideTheContentCell() =>
        AssertEveryPageReachable("English");

    /// <summary>
    /// The sweep behind <see cref="EveryHandChunkedPage_KeepsItsDwellCirclesInsideTheContentCell"/>,
    /// in whatever language <see cref="EyeCtrl.Core.Localization.StringTable.Current"/> resolves —
    /// the sources build their rows from it. T09's pseudo-locale gate runs the same sweep with the
    /// pseudo-locale activated, so the reachability invariant is ONE rule in both languages, and
    /// it also asserts that the chunker leaves no page without a row (an empty page is a page turn
    /// to nothing).
    /// </summary>
    /// <param name="language">The language the sweep runs in, for the failure messages.</param>
    internal static void AssertEveryPageReachable(string language)
    {
        var root = SettingsContentFitTests.Xml();
        var budget = ContentHeightBudget(root);
        var columnWidth = ContentColumnWidth(root);

        var unreachable = new List<Unreachable>();
        var tooClose = new List<ClearancePair>();
        var duplicateIds = new List<DuplicateId>();
        var emptyPages = new List<string>();
        var scopes = new List<IDisposable>();
        var usedReachExemptions = new HashSet<ReachExemption>();
        var usedClearanceExemptions = new HashSet<ClearanceExemption>();
        var pagesChecked = 0;
        var viewsDeclared = new HashSet<string>(StringComparer.Ordinal);
        var viewsWithPages = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var type in AllSourceTypes())
            {
                foreach (var (fixture, source, scope) in FixturesOf(type))
                {
                    if (scope is not null) scopes.Add(scope);

                    foreach (var (view, built) in ViewsOf(source))
                    {
                        var pages = PagesAsRendered(source, built, budget, columnWidth);
                        var firstPageOfId = new Dictionary<string, int>(StringComparer.Ordinal);
                        var viewKey = $"{type.Name}.{view}";
                        viewsDeclared.Add(viewKey);
                        if (pages.Count > 0) viewsWithPages.Add(viewKey);

                        for (var p = 0; p < pages.Count; p++)
                        {
                            if (!pages[p].Rows.Any(row => row is not SeparatorSpec))
                                emptyPages.Add($"  {viewKey} [{fixture}] page {p + 1}/{pages.Count}");

                            var (pageHeight, targets) = Place(pages[p], columnWidth);
                            pagesChecked++;

                            foreach (var t in targets)
                            {
                                if (firstPageOfId.TryGetValue(t.DwellId, out var firstPage))
                                    duplicateIds.Add(new DuplicateId(
                                        type.Name, view, fixture, t.DwellId, firstPage, p + 1));
                                else
                                    firstPageOfId[t.DwellId] = p + 1;

                                var left = t.CentreX - RadiusPhysical;
                                var right = t.CentreX + RadiusPhysical;
                                var top = t.CentreY - RadiusPhysical;
                                var bottom = t.CentreY + RadiusPhysical;

                                var beyondBottom = bottom > budget + Tolerance;
                                var beyondLeft = left < -Tolerance;
                                var beyondRight = right > columnWidth + Tolerance;
                                var beyondTop = top < -Tolerance;
                                if (!beyondBottom && !beyondLeft && !beyondRight && !beyondTop)
                                    continue;

                                // Only a bottom overrun is exemptible — the one accepted page
                                // crosses the fold, never a side. An exempted id that also left
                                // the column, or a page that got worse than its measurement,
                                // falls through to the failure list.
                                var exemption = !beyondLeft && !beyondRight && !beyondTop && beyondBottom
                                    ? ReachExemptions.FirstOrDefault(e =>
                                        e.Source == type.Name && e.View == view && e.DwellId == t.DwellId)
                                    : null;
                                if (exemption is not null)
                                {
                                    usedReachExemptions.Add(exemption);
                                    if (bottom - budget <= exemption.MeasuredOverflowDip + Tolerance)
                                        continue;
                                }

                                unreachable.Add(new Unreachable(
                                    type.Name, view, fixture, p + 1, pages.Count, pageHeight,
                                    t.DwellId, left, right, top, bottom));
                            }

                            for (var i = 0; i < targets.Count; i++)
                            for (var j = i + 1; j < targets.Count; j++)
                            {
                                var a = targets[i];
                                var b = targets[j];
                                var dx = a.CentreX - b.CentreX;
                                var dy = a.CentreY - b.CentreY;
                                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                                var clearance = distance - (2 * RadiusPhysical);

                                // A pair that clears the base floor needs no exemption and marks
                                // none used — that is what makes a stale entry fail the
                                // both-direction check after the geometry is fixed.
                                if (clearance >= ClearanceFloorDip - Tolerance) continue;

                                var exemption = FindClearanceExemption(a.DwellId, b.DwellId);
                                if (exemption is not null)
                                {
                                    usedClearanceExemptions.Add(exemption);
                                    if (clearance >= exemption.MeasuredClearanceDip - Tolerance)
                                        continue;
                                }

                                tooClose.Add(new ClearancePair(
                                    type.Name, view, fixture, p + 1, pages.Count,
                                    a.DwellId, a.CentreX, a.CentreY,
                                    b.DwellId, b.CentreX, b.CentreY, distance));
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (var scope in scopes) scope.Dispose();
        }

        var context = string.Format(
            CultureInfo.InvariantCulture,
            "({7}: budget {0:F2} DIP, {1:F0}x{2:F0} display, {3:F2} DIP content column, "
            + "circle radius {4:F2}, clearance floor {5:F2}, {6} pages checked)",
            budget, DisplayWidthDip, DisplayHeightDip, columnWidth, RadiusPhysical,
            ClearanceFloorDip, pagesChecked, language);

        Check.WithCustomMessage(
                $"These pages hold no row (or only a rule) {context}:{Environment.NewLine}"
                + string.Join(Environment.NewLine, emptyPages)
                + $"{Environment.NewLine}A page with nothing on it is a page turn to nothing.")
            .That(emptyPages).IsEmpty();

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "These dwell circles are NOT wholly inside the content cell {0}:{1}{2}{1}"
                + "The content area has no ScrollViewer, so a circle past the fold cannot be "
                + "looked at — the setting is registered and unreachable, which for a gaze-only "
                + "user is the same as gone. Every page shape here is either hand-chunked (its "
                + "source returns PrefersAutoPagination == false, so MaybeRepaginateByHeight "
                + "passes it through untouched and no amount of chunker fixing helps) or the "
                + "post-chunk output of the chunker itself. For a hand-chunked page, reduce the "
                + "source's own per-page count (QuickActionsPageSource.QaConfigRowsPerPage, "
                + "QaActionsPerPage or QaPresetsPerPage — mind that an ACTION contributes two "
                + "rows, a toggle and a Delete), reorder the rows so the ones that matter lead, "
                + "or make the source opt in to auto-pagination if its page boundaries carry no "
                + "navigation semantics. For an auto-paginated page, the row that overflowed is "
                + "one the chunker believes fits but which ARRANGES taller than it MEASURES — "
                + "fix the row's measurement/arrangement parity rather than the chunker. Do NOT "
                + "relax this into a page-height check: see this class's remarks for why the "
                + "type-picker page legitimately overflows its cell while its circles stay "
                + "aimable. And "
                + "there is no general known-unreachable list to add these to — the ratchet that "
                + "held issue #113's three offenders was retired WITH them, and the two named "
                + "exemptions above carry measurements and ratchet in both directions. An "
                + "exemption is a decision to argue for in a diff, not a way to go green.",
                context, Environment.NewLine,
                string.Join(Environment.NewLine, unreachable.Select(o => o.Describe()))))
            .That(unreachable).IsEmpty();

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "These pairs of registered targets leave less than the {0:F2} DIP clearance floor "
                + "between their hit circles {1}:{2}{1}"
                + "The floor is the colour-swatch grid's own vertical pitch (a swatch cell plus "
                + "its two gutters, less one circle diameter), the only adjacent-target gap in "
                + "the application already pinned by a geometry test. A dwell target is a centre "
                + "plus a radius, so two circles closer than that put a neighbour's target within "
                + "one gaze drift of the one being aimed at. Fix the row's own geometry (add room "
                + "between the controls), or — if the geometry is a deliberately accepted, "
                + "pre-existing pitch — add a named ClearanceExemption carrying the measured "
                + "number; note the exemption applies to the pair it names and FAILS once the "
                + "pair stops needing it.",
                ClearanceFloorDip, Environment.NewLine,
                string.Join(Environment.NewLine, tooClose.Select(o => o.Describe()))))
            .That(tooClose).IsEmpty();

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "These dwell ids are registered on more than one page of the same view {0}:{1}{2}{1}"
                + "Pages of one view are the same surface at different scroll positions, built "
                + "from one source state; an id that appears twice means two pages describe the "
                + "same target with different geometry or actions, and which one fires depends on "
                + "which page-turn the user happens to be on.",
                context, Environment.NewLine,
                string.Join(Environment.NewLine, duplicateIds.Select(o => o.Describe()))))
            .That(duplicateIds).IsEmpty();

        // Both-direction ratchets for the named exemptions. A target or pair that no longer
        // needs its exemption fails here until the entry is deleted, exactly as the retired
        // known-unreachable list demanded; the reverse direction (an offender getting worse) is
        // enforced at the point of use, by the measured numbers.
        var staleReachExemptions = ReachExemptions.Where(e => !usedReachExemptions.Contains(e)).ToList();
        Check.WithCustomMessage(
                "These reachability exemptions were never needed: the targets they name now fit "
                + "the content cell, or no longer exist under those names. That is the moment the "
                + "entry is supposed to be deleted — the exemption list must not outlive its "
                + "offenders. Delete the entry (the file's retirement note explains why an "
                + "exemption left behind is a place to hide the next failure).")
            .That(staleReachExemptions.Select(e => $"{e.Source}.{e.View}.{e.DwellId}")).IsEmpty();

        var staleClearanceExemptions =
            ClearanceExemptions.Where(e => !usedClearanceExemptions.Contains(e)).ToList();
        Check.WithCustomMessage(
                "These clearance exemptions were never needed: the pairs they name now clear the "
                + "floor, or their prefixes no longer exist. That is the moment the entry is "
                + "supposed to be deleted — the exemption list must not outlive its offenders. "
                + "Delete the entry (the file's retirement note explains why an exemption left "
                + "behind is a place to hide the next failure).")
            .That(staleClearanceExemptions.Select(e => string.Join(", ", e.IdPrefixes))).IsEmpty();

        // The whole sweep, not just one page of it: a view that emitted no page — a source
        // gutted to return an empty list, a fixture that stopped reaching a page — would
        // otherwise be measured by nothing at all while the per-page assertions above pass
        // vacuously over it.
        Check.WithCustomMessage(
                "No page was measured for these declared views. The sources were discovered and "
                + "constructed and the views were driven, so this means each emitted zero pages — "
                + "every assertion above then passed over nothing for it.")
            .That(viewsDeclared.Where(v => !viewsWithPages.Contains(v)))
            .IsEmpty();

        Check.WithCustomMessage(
                "No settings page was measured at all. The sources were discovered and "
                + "constructed, so this means every one of them emitted zero pages — every "
                + "assertion above then passed over nothing.")
            .That(pagesChecked).IsStrictlyGreaterThan(0);
    }

    /// <summary>
    /// The named clearance exemption covering this pair, or null. The match is on BOTH ids
    /// carrying the same exempt prefix — <c>{prefix}_{index}</c> is the id shape every composite
    /// in this application uses for its parts — so a pair from some other control, in the same
    /// source or any other, is not covered by a prefix it does not carry.
    /// </summary>
    /// <param name="firstId">One target's dwell id.</param>
    /// <param name="secondId">The other target's dwell id.</param>
    /// <returns>The exemption, or null.</returns>
    private static ClearanceExemption? FindClearanceExemption(string firstId, string secondId) =>
        ClearanceExemptions.FirstOrDefault(e =>
            e.IdPrefixes.Any(p => firstId.StartsWith(p + "_", StringComparison.Ordinal)
                                  && secondId.StartsWith(p + "_", StringComparison.Ordinal)));

    /// <summary>One unreachable trailing page, with enough context to name it in a failure.</summary>
    private sealed record Offender(
        string Source, string View, string Fixture, int Page, int Pages, double PageHeight,
        string DwellId, double CentreY)
    {
        /// <summary>Renders this offender as one line of a failure message.</summary>
        /// <returns>The formatted line.</returns>
        public string Describe() => string.Format(
            CultureInfo.InvariantCulture,
            "  {0}.{1} [{2}] page {3}/{4} (page height {5:F2}): \"{6}\" centre {7:F2}",
            Source, View, Fixture, Page, Pages, PageHeight, DwellId, CentreY);
    }

    /// <summary>
    /// Half of the 150 DIP row pitch: the least slack every CHUNKED quick-actions page keeps
    /// between its page bottom and the content budget. All of them currently leave 88 (four
    /// rows against the 688 DIP cell); see <see cref="QaPresetsPerPage"/>'s own remarks for why
    /// 13 — what a fifth row's last circle would leave — "is not a margin to rely on".
    /// </summary>
    private const double MinTrailingSlackDip = 75;

    /// <summary>
    /// The trailing-margin companion to the reachability guard: every page whose shape one of
    /// <c>QuickActionsPageSource</c>'s chunk constants controls must keep at least
    /// <see cref="MinTrailingSlackDip"/> of slack below its last row.
    ///
    /// <para>
    /// Reachability alone gates at FIVE rows — the last circle's centre lands at 675 against
    /// the 688 budget and the guard above stays green (measured during the PR #155 review:
    /// <c>QaConfigRowsPerPage = 5</c> passed it; only 6 went red). But the constants' own
    /// comments all advertise the fuller shape — four rows, 88 to spare — and
    /// <see cref="QaPresetsPerPage"/>'s remarks reject exactly the 13-DIP margin a fifth row
    /// would leave. This pins the advertised shape, so a future "five into four is tidier"
    /// edit fails here instead of silently shaving the margin the comments claim.
    /// </para>
    /// <para>
    /// ONE page is out of scope BY NAME: the type picker (<c>QaView.PickType</c>), whose
    /// hard-wired Back + four type buttons is the 13-DIP page this class's remarks document
    /// as legitimately reachable-but-overflowing, and which the reachability guard's own named
    /// <see cref="ReachExemptions"/> entry carries with its measurement. Re-chunking the tab's
    /// entry point is a UX decision this guard does not take; the exclusion is a named view
    /// with its reason stated, not an anonymous entry in a suppression list.
    /// </para>
    /// </summary>
    [StaFact]
    public void EveryChunkedQuickActionsPage_KeepsHalfARowOfTrailingSlack()
    {
        var root = SettingsContentFitTests.Xml();
        var budget = ContentHeightBudget(root);
        var columnWidth = ContentColumnWidth(root);

        var offenders = new List<Offender>();
        var pagesChecked = 0;

        foreach (var (fixture, source, _) in FixturesOf(typeof(QuickActionsPageSource)))
        {
            foreach (var (view, pages) in ViewsOf(source))
            {
                if (view == nameof(QuickActionsPageSource.QaView.PickType)) continue;

                for (var p = 0; p < pages.Count; p++)
                {
                    var (pageHeight, _) = Place(pages[p], columnWidth);
                    pagesChecked++;

                    if (pageHeight > budget - MinTrailingSlackDip)
                        offenders.Add(new Offender(
                            nameof(QuickActionsPageSource), view, fixture, p + 1, pages.Count,
                            pageHeight, "(page bottom)", pageHeight));
                }
            }
        }

        var context = string.Format(
            CultureInfo.InvariantCulture,
            "(budget {0:F2} DIP, slack floor {1:F0} DIP, {2:F2} DIP content column, "
            + "{3} pages checked)",
            budget, MinTrailingSlackDip, columnWidth, pagesChecked);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "These hand-chunked pages leave less than half a row of slack below their last "
                + "row {0}:{1}{2}{1}Reachability alone tolerates a fifth row — its last circle "
                + "still lands inside the cell — but the chunk constants' own remarks reject that "
                + "13 DIP margin as one the restyle could shave again tomorrow. Reduce the "
                + "source's per-page count (QuickActionsPageSource.QaConfigRowsPerPage, "
                + "QaActionsPerPage or QaPresetsPerPage) until the page keeps its advertised "
                + "88 DIP of slack.",
                context, Environment.NewLine,
                string.Join(Environment.NewLine, offenders.Select(o => o.Describe()))))
            .That(offenders).IsEmpty();

        Check.WithCustomMessage(
                "No chunked page was measured — the trailing-slack guard swept an empty set.")
            .That(pagesChecked).IsStrictlyGreaterThan(0);
    }

    /// <summary>
    /// The Keyboard tab's learned-words page only exists when the context carries a real
    /// <see cref="UserDictionaryService"/>, so the gate builds one over unique temp dirs — the
    /// fixture shape <c>KeyboardDictionaryPageTests</c> uses — with eight learned words, enough
    /// that the page is several chunk pages long. Disposed with the test; a leaked temp dir per
    /// run is the alternative.
    /// </summary>
    private sealed class LearnedWordsFixture : IDisposable
    {
        private readonly string _baseDir;
        private readonly string _appDataDir;

        /// <summary>The service the Keyboard source builds its learned-words page through.</summary>
        public UserDictionaryService Service { get; }

        /// <summary>Writes the base dictionary and a user dictionary with eight learned words.</summary>
        public LearnedWordsFixture()
        {
            _baseDir = Path.Combine(Path.GetTempPath(), "eyectrl_reach_kb_" + Guid.NewGuid());
            _appDataDir = Path.Combine(Path.GetTempPath(), "eyectrl_reach_kb_app_" + Guid.NewGuid());
            Directory.CreateDirectory(Path.Combine(_baseDir, "Resources", "Dictionaries"));
            Directory.CreateDirectory(_appDataDir);

            // "hello" is base-dictionary vocabulary, so it is not learned; the other eight are
            // the set difference the page lists, most-used first.
            File.WriteAllText(
                Path.Combine(_baseDir, "Resources", "Dictionaries", "en.json"),
                JsonSerializer.Serialize(new { words = new Dictionary<string, int> { ["hello"] = 100 } }));
            File.WriteAllText(
                Path.Combine(_appDataDir, "user_dictionary_en.json"),
                JsonSerializer.Serialize(new Dictionary<string, int>
                {
                    ["hello"] = 100, ["aardvark"] = 40, ["zeppelin"] = 36, ["marigold"] = 32,
                    ["trombone"] = 28, ["quokka"] = 24, ["nectarine"] = 20, ["windmill"] = 16,
                    ["yodel"] = 12,
                }));

            var settings = new EyeCtrlSettings();
            settings.Keyboard.LanguagePack = "en";
            Service = new UserDictionaryService(settings, _appDataDir, _baseDir);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            try { Directory.Delete(_baseDir, recursive: true); } catch (IOException) { }
            try { Directory.Delete(_appDataDir, recursive: true); } catch (IOException) { }
        }
    }
}
