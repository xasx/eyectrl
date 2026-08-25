namespace EyeCtrl.App.Tests.Settings;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Xml.Linq;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Every dwell target on a HAND-CHUNKED settings page has to sit inside the content cell the
/// window actually gives it.
///
/// <para>
/// **The hole this closes.** <c>SettingsAutoPagination</c> re-splits a tab's rows to fit
/// <c>ContentAvailableHeight</c>, and <c>SettingsAutoPaginationTests</c> covers that chunker
/// thoroughly — but only over pages the chunker built. A source whose
/// <see cref="ISettingsPageSource.PrefersAutoPagination"/> is <c>false</c> emits its OWN pages and
/// <c>MaybeRepaginateByHeight</c> returns them untouched, so NOTHING measures them against the
/// budget. Every existing height guard is therefore structurally blind to them, and the content
/// area has no <c>ScrollViewer</c> (SettingsWindow.xaml: "no ScrollViewer — paginated"), so a row
/// past the fold is not merely awkward: its hit circle is registered over pixels the user cannot
/// look at. For a user whose only input channel is gaze, a registered-but-unreachable target is
/// a setting that has silently ceased to exist.
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
/// **This asserts REACHABILITY, not page height — do not "tighten" it into a height check.**
/// The two are different invariants and the weaker-looking one is the correct one. Measured here
/// at the 1424 DIP content column against the 688 DIP budget, the quick-action TYPE picker's page
/// is 750 DIP: it overflows the cell by 62, and yet all five of its dwell circles are reachable
/// (centres 75, 225, 375, 525, 675 — the last one 13 DIP inside the budget). What is clipped is
/// the trailing margin below the final button, and nothing is registered there. A
/// "page height &lt;= budget" assertion would fail that page and would have to be relaxed or
/// suppressed, which is how a guard gets deleted. The circle centres are what the user has to be
/// able to aim at, so the circle centres are what is asserted.
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
/// **It is UNCONDITIONAL, and it did not start that way.** It was born a two-directional ratchet
/// over three targets that were already unreachable before the restyle — <c>qa_add</c> on the
/// quick-action List page, and the <c>qa_del_</c>/<c>qa_en_</c> rows on its per-action pages,
/// each measured on <c>main @ dd1baf5c</c> rather than assumed. Fixing them meant reordering a
/// tab or spending an extra dwell page-turn, which is a decision about this application's only
/// input channel and was not the restyle's to take. All three are now fixed, and because the
/// ratchet failed in BOTH directions it was the guard itself that demanded each entry's deletion
/// as its bug closed. The list went with the last entry: see the retirement note above the guard
/// for why an empty one would have been worse than none.
/// </para>
/// <para>
/// Nothing is shown and no input is simulated: a detached element tree renders no window. No UI
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
    /// </para>
    /// <para>
    /// KNOWN GAP, recorded not solved: on a genuinely small display this tab therefore has
    /// unreachable dwell targets that no per-page constant can fix, and this guard will not see
    /// them. It is the last part of issue #113 still open — the three targets that WERE tracked
    /// here are fixed — and it is scoped out by display size, not by any exception list. Do not
    /// attempt it by widening the assertion here, which would only make the guard unsatisfiable
    /// and invite its deletion.
    /// </para>
    /// </summary>
    private const double DisplayWidthDip = 1920;

    /// <inheritdoc cref="DisplayWidthDip"/>
    private const double DisplayHeightDip = 1080;

    private const double RadiusPhysical = SettingsWindow.ControlBtnRadius;
    private const int DwellMs = 800;

    /// <summary>
    /// The content cell's height in DIP — every term of <c>SettingsWindow.ContentAvailableHeight</c>,
    /// read off the live XAML and off production's own consts.
    /// </summary>
    /// <param name="root">The parsed SettingsWindow.xaml root.</param>
    /// <returns>The height budget in DIP.</returns>
    private static double ContentHeightBudget(XElement root)
    {
        var header = SettingsContentFitTests.Margin(root, "HeaderPanel");
        var contentArea = SettingsContentFitTests.Margin(root, "ContentArea");
        var cardPadding = SettingsContentFitTests.Padding(root, "ContentCard");
        var pagination = SettingsContentFitTests.Margin(root, "PaginationPanel");

        return SettingsContentFitTests.WindowHeightFor(DisplayHeightDip)
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
    private static double ContentColumnWidth(XElement root)
    {
        var contentArea = SettingsContentFitTests.Margin(root, "ContentArea");
        var cardPadding = SettingsContentFitTests.Padding(root, "ContentCard");
        return SettingsContentFitTests.WindowWidthFor(DisplayWidthDip)
            - SettingsContentFitTests.SidebarWidth(root)
            - contentArea.Left - contentArea.Right
            - cardPadding.Left - cardPadding.Right;
    }

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
    /// <returns>The constructed source.</returns>
    private static ISettingsPageSource Construct(Type type, SettingsWindowViewModel viewModel)
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
                + "build it — and a hand-chunked page this harness cannot build is a page nothing "
                + "measures against the height budget. Teach the harness to construct it; do NOT "
                + "filter it out.")
            .That(ctor).IsNotNull();

        var ctx = TestSettingsPageContext.Build(viewModel: viewModel);
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
    /// pages this harness would never have built.
    /// </para>
    /// </summary>
    /// <param name="type">The source type.</param>
    /// <returns>(fixture name, constructed source) per fixture.</returns>
    private static IEnumerable<(string Fixture, ISettingsPageSource Source)> FixturesOf(Type type)
    {
        yield return ("nothing configured", Construct(type, QaTestSupport.MakeViewModel()));

        if (type != typeof(QuickActionsPageSource)) yield break;

        var configured = QaTestSupport.MakeViewModel();
        for (var i = 0; i < SeededActionCount; i++)
            configured.QuickActionsEditor.AddKeyboardShortcut("Action " + i, "Ctrl+" + i);
        yield return ($"{SeededActionCount} actions configured", Construct(type, configured));
    }

    /// <summary>
    /// Every page set a source can put on screen, one entry per navigable sub-view.
    ///
    /// <para>
    /// Driving the sub-views is load-bearing, not thoroughness for its own sake:
    /// <c>QuickActionsPageSource</c>'s preset pages — the ones issue #113 was about — exist ONLY
    /// inside the picker views. The List view alone never builds a <c>ChunkPresets</c> page at
    /// all, so a harness that only called <c>Build()</c> on a freshly constructed source would
    /// miss the bug entirely while appearing to cover the tab.
    /// </para>
    /// <para>
    /// The sub-view state machine is per-source and there is no interface member for it, so the
    /// one source that has one is driven explicitly. An opted-out source this method does not know
    /// how to drive FAILS in <see cref="TheHarness_CanDriveEveryHandChunkedSource"/> rather than
    /// being quietly reduced to its default view.
    /// </para>
    /// </summary>
    /// <param name="source">The constructed source.</param>
    /// <returns>(view name, pages) per sub-view.</returns>
    private static IEnumerable<(string View, IReadOnlyList<PageSpec> Pages)> ViewsOf(
        ISettingsPageSource source)
    {
        if (source is QuickActionsPageSource qa)
        {
            foreach (var view in Enum.GetValues<QuickActionsPageSource.QaView>())
            {
                qa.GoTo(view);   // internal, and reachable: EyeCtrl.App.Tests has App internals.
                yield return (view.ToString(), qa.Build());
            }
            yield break;
        }

        yield return ("(default)", source.Build());
    }

    /// <summary>True for the sources this guard exists for: the ones nothing re-paginates.</summary>
    /// <param name="source">The constructed source.</param>
    /// <returns>True when the source keeps its own page boundaries.</returns>
    private static bool IsHandChunked(ISettingsPageSource source) => !source.PrefersAutoPagination;

    // ────── Placement ──────

    /// <summary>One dwell target, as its arranged vertical centre in content-panel coordinates.</summary>
    private sealed record Target(string DwellId, double CentreY);

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
                var top = circle.Root
                    .TransformToAncestor(host)
                    .TransformBounds(new Rect(circle.Root.RenderSize));
                targets.Add(new Target(circle.DwellId, top.Top + (top.Height / 2)));
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
                + "implementation. The settings window has eleven tabs, so this is a broken "
                + "discovery query, not an empty codebase — and every assertion in this file "
                + "would otherwise iterate nothing and pass.")
            .That(types).Not.IsEmpty();
    }

    /// <summary>
    /// At least one source must be hand-chunked, and this harness must know how to drive each one
    /// that is.
    ///
    /// <para>
    /// Two failure modes, one assertion each. If <c>PrefersAutoPagination</c> became true
    /// everywhere the fit assertion below would sweep zero pages and stay green; and if a NEW
    /// hand-chunked source appears with its own sub-view state machine,
    /// <see cref="ViewsOf"/> would silently reduce it to its default view — measuring the one
    /// page that probably fits and none of the ones that might not.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheHarness_CanDriveEveryHandChunkedSource()
    {
        var handChunked = AllSourceTypes()
            .Select(t => (Type: t, Source: Construct(t, QaTestSupport.MakeViewModel())))
            .Where(p => IsHandChunked(p.Source))
            .ToList();

        Check.WithCustomMessage(
                "No ISettingsPageSource reports PrefersAutoPagination == false, so this whole "
                + "file now measures nothing. If the last hand-chunked source really did opt in to "
                + "height-based re-pagination, delete these tests deliberately with that reason "
                + "written down — do not leave a green guard over an empty set.")
            .That(handChunked).Not.IsEmpty();

        // ViewsOf knows one sub-view state machine. Any other hand-chunked source would be
        // reduced to its default Build(), which is the blindness this file exists to remove.
        var undrivable = handChunked
            .Where(p => p.Type != typeof(QuickActionsPageSource))
            .Select(p => p.Type.Name)
            .ToList();

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "{0} is hand-chunked but HandChunkedPageFitTests.ViewsOf has no driver for it, so "
                + "only its default view would be measured. If it has navigable sub-views (the way "
                + "QuickActions has QaView), drive them there — its picker pages are precisely the "
                + "ones no height budget checks. If it genuinely has one view, add it to this "
                + "exemption with that stated.",
                string.Join(", ", undrivable)))
            .That(undrivable).IsEmpty();
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
    // and that is the point. An empty ratchet still compiles into somewhere to PUT a failure: the
    // next person to break a page could add an entry and go green, and the file's own remarks
    // would read as licence to. With it gone the assertion below is unconditional, so the only
    // way past a failure is to fix the page or to rebuild this mechanism on purpose — a visible
    // act, in a diff someone reviews. That is strictly the stronger guard, and it is the state
    // this file was always meant to reach.
    //
    // If a genuinely pre-existing unreachable target is ever found again, the pattern is worth
    // restoring rather than reinventing: git log this file back past issue #113. What made it
    // work was the equality (a ceiling would let a prefix entry absorb three more broken rows in
    // silence), the per-view key (a blanket "ignore the List view" would blind the guard to a
    // real regression), and the requirement that every entry carry the measurement proving it
    // predates the change that exposed it — the first draft of that list claimed these were
    // restyle regressions, and measurement showed they were not.
    //
    // NOT retired: the narrow-display gap in DisplayWidthDip's remarks. That one is scoped out by
    // display size, never by this list, and it is still open on issue #113.


    // ────── The guard ──────

    /// <summary>One unreachable target, with enough context to name it in a failure.</summary>
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
    /// The invariant: on every page every hand-chunked source emits, in every sub-view, under
    /// every fixture, each registered dwell circle's arranged centre sits inside the content
    /// height budget. No exceptions and nowhere to record one — see the retirement note above the
    /// guard for why the ratchet that used to carry three is gone.
    /// </summary>
    [StaFact]
    public void EveryHandChunkedPage_KeepsItsDwellCirclesInsideTheContentCell()
    {
        var root = SettingsContentFitTests.Xml();
        var budget = ContentHeightBudget(root);
        var columnWidth = ContentColumnWidth(root);

        var offenders = new List<Offender>();
        var pagesChecked = 0;

        foreach (var type in AllSourceTypes())
        {
            foreach (var (fixture, source) in FixturesOf(type))
            {
                if (!IsHandChunked(source)) break;   // per-type property; no fixture changes it.

                foreach (var (view, pages) in ViewsOf(source))
                {
                    for (var p = 0; p < pages.Count; p++)
                    {
                        var (pageHeight, targets) = Place(pages[p], columnWidth);
                        pagesChecked++;

                        offenders.AddRange(targets
                            .Where(t => t.CentreY > budget)
                            .Select(t => new Offender(
                                type.Name, view, fixture, p + 1, pages.Count, pageHeight,
                                t.DwellId, t.CentreY)));
                    }
                }
            }
        }

        var context = string.Format(
            CultureInfo.InvariantCulture,
            "(budget {0:F2} DIP, {1:F0}x{2:F0} display, {3:F2} DIP content column, "
            + "{4} pages checked)",
            budget, DisplayWidthDip, DisplayHeightDip, columnWidth, pagesChecked);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "These dwell circles are registered BELOW the content cell {0}:{1}{2}{1}"
                + "The content area has no ScrollViewer, so a circle past the fold cannot be "
                + "looked at — the setting is registered and unreachable, which for a gaze-only "
                + "user is the same as gone. These pages are HAND-CHUNKED: their source returns "
                + "PrefersAutoPagination == false, so MaybeRepaginateByHeight passes them through "
                + "untouched and no amount of chunker fixing helps. Reduce the source's own "
                + "per-page count (QuickActionsPageSource.QaConfigRowsPerPage, QaActionsPerPage "
                + "or QaPresetsPerPage — mind that an ACTION contributes two rows, a toggle and a "
                + "Delete) until the last circle fits, reorder the rows so the ones that matter "
                + "lead, or make the source opt in to auto-pagination if its page boundaries "
                + "carry no navigation semantics. Do NOT relax this into a page-height check: see "
                + "this class's remarks for why the type picker legitimately overflows its cell "
                + "while every one of its circles stays reachable. And there is deliberately no "
                + "known-unreachable list to add these to — the ratchet that held issue #113's "
                + "three offenders was retired WITH them, precisely so that a failure here has "
                + "nowhere to go but a fix. Reintroducing one is a decision to argue for in a "
                + "diff, not a way to go green.",
                context, Environment.NewLine,
                string.Join(Environment.NewLine, offenders.Select(o => o.Describe()))))
            .That(offenders).IsEmpty();


        Check.WithCustomMessage(
                "No hand-chunked page was measured at all. The sources were discovered and "
                + "constructed, so this means every one of them emitted zero pages — every "
                + "assertion above then passed over nothing.")
            .That(pagesChecked).IsStrictlyGreaterThan(0);
    }
}
