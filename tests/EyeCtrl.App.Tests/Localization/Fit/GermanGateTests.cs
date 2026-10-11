namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Settings;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;

/// <summary>
/// T10: T09's binding layout gates, run on the German draft under a TEST manifest that switches
/// every surface on (<see cref="GermanDraft"/>). The shipped manifest stays empty — these rehearse
/// T11–T13's switch-on in the test process only.
///
/// <para>
/// Covered here: the draft is complete and every surface becomes effective; every Settings page
/// paginates and stays reachable; the sidebar keeps every tab where it was; the pager fits; the
/// TrackStatus rows fit. The per-class fit is <see cref="GermanFitTests"/>; geometry invariance and
/// the Settings modal's exception take German as one more table in
/// <see cref="GeometryInvarianceTests"/> and <see cref="SettingsModalExceptionTests"/>; the
/// Supervisor's classes and setup window are checked in EyeCtrl.Supervisor.Tests.
/// </para>
///
/// <para>
/// The Settings sources read <see cref="StringTable.Current"/>, so the German table is activated
/// globally: the exclusive "StringTable Tests (App)" collection, with English restored after each.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class GermanGateTests
{
    private readonly ITestOutputHelper _output;

    public GermanGateTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Every English key has German, the test table loads it without a warning, and every surface
    /// becomes effective — so the gates below measure German everywhere, not English fallbacks.
    /// </summary>
    [Fact]
    public void TheDraft_CoversEveryKey_AndEverySurfaceSwitchesOn()
    {
        Check.WithCustomMessage("Keys with no German: " + string.Join(", ", GermanDraft.MissingKeys()))
            .That(GermanDraft.MissingKeys()).IsEmpty();
        Check.WithCustomMessage("The German test table warned while loading:\n  " + string.Join("\n  ", GermanDraft.LoadWarnings))
            .That(GermanDraft.LoadWarnings).IsEmpty();

        var table = GermanDraft.Table;
        var notGerman = new List<string>();
        foreach (var surface in GermanDraft.TestManifest)
        {
            if ((table.FormatProviderFor(surface) as System.Globalization.CultureInfo)?.Name != "de-DE") notGerman.Add($"{surface}: formats invariant, so it is not effective");
        }

        // A sample per surface resolves the German text, not the English.
        foreach (var key in new[] { "supervisor.fallback.title", "app-tray.exit", "caregiver-alert.title", "drift-notice.message",
                     "startup-error.title", "track-status.title", "language-recovery.prompt.restart", "settings.tab.general.label",
                     "calibration.window.retry", "eye-care.banner.message", "overlays.ring.leftClick" })
        {
            if (table.T(key) == StringTable.EmbeddedEnglish.T(key)) notGerman.Add($"{key} resolves its English \"{table.T(key)}\"");
        }

        Check.WithCustomMessage(string.Join("\n", notGerman)).That(notGerman).IsEmpty();
    }

    /// <summary>Binding: under German every Settings page paginates and every row stays reachable.</summary>
    [StaFact]
    public void EverySettingsPage_InGerman_PaginatesAndStaysReachable()
    {
        StringTable.Activate(GermanDraft.Table);
        try
        {
            Check.That(StringTable.Current.T("settings.tab.general.label")).IsEqualTo("Allgemein");
            HandChunkedPageFitTests.AssertEveryPageReachable("German");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>Every theme × both row forms: two-column at the production column, stacked at the narrowest.</summary>
    public static TheoryData<string, string> ThemesAndRowForms()
    {
        var data = new TheoryData<string, string>();
        foreach (var theme in ThemeRegistry.Names)
        {
            data.Add(theme, "two-column");
            data.Add(theme, "stacked");
        }

        return data;
    }

    /// <summary>
    /// Binding (the coordinator's call after mutant M10c): every German text on every Settings page
    /// shows whole sideways — a wrapping row name, description or note has no unbreakable run (a
    /// U+00AD break counts, with its hyphen) wider than its column, and a one-line text is not
    /// clipped. Both row forms, in every theme. See <see cref="HandChunkedPageFitTests.RowTextOverruns"/>.
    /// </summary>
    [StaTheory]
    [MemberData(nameof(ThemesAndRowForms))]
    public void EverySettingsText_InGerman_ShowsWholeSideways(string theme, string form) =>
        AssertRowTextsShowWhole(GermanDraft.Table, theme, form);

    /// <summary>The same sweep on the English: the baseline the German gate is measured against.</summary>
    [StaTheory]
    [MemberData(nameof(ThemesAndRowForms))]
    public void EverySettingsText_InEnglish_ShowsWholeSideways(string theme, string form) =>
        AssertRowTextsShowWhole(StringTable.EmbeddedEnglish, theme, form);

    private void AssertRowTextsShowWhole(StringTable strings, string theme, string form)
    {
        var width = form == "stacked"
            ? HandChunkedPageFitTests.StackedColumnWidth
            : HandChunkedPageFitTests.ContentColumnWidth(SettingsContentFitTests.Xml());
        Check.WithCustomMessage($"The {form} column ({FitText.Dip(width)} DIP) does not give the {form} row form.")
            .That(width >= SettingsWindow.TwoColumnMinimumWidth).IsEqualTo(form == "two-column");

        // ThemeScope's users belong in "ActiveTheme"; this class is in "StringTable Tests (App)".
        // Safe: both are DisableParallelization, which xUnit v3 runs alone, beside no other test
        // (pinned by NonParallelCollectionPin), so the theme cannot change under a parallel reader.
        using var scope = ThemeScope.Activate(ThemeRegistry.Resolve(theme));
        StringTable.Activate(strings);
        try
        {
            var findings = HandChunkedPageFitTests.RowTextOverruns(width, out var measured);
            _output.WriteLine($"{theme}, {form} ({FitText.Dip(width)} DIP): {measured} texts measured, {findings.Count} overrun");
            Check.WithCustomMessage("Too few Settings texts were measured: the sweep is not seeing the pages.")
                .That(measured).IsStrictlyGreaterThan(300);
            Check.WithCustomMessage($"Settings texts overrun their column ({theme}, {form}):\n  " + string.Join("\n  ", findings))
                .That(findings).IsEmpty();
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// Binding: every German tab label stays on one line in production's tab face, keeps the
    /// sidebar's pitch, and leaves the tab's box exactly where the English one is.
    /// </summary>
    [StaFact]
    public void TheSidebar_InGerman_KeepsEveryTabWhereItWas()
    {
        var problems = new List<string>();
        var translated = 0;
        var pitch = SettingsWindow.TabContainerHeight + 2 * SettingsWindow.TabButtonMargin;
        foreach (var tab in SettingsTabDriftTests.RenderedTabDefs())
        {
            var german = GermanDraft.Table.T(tab.LabelKey);
            Check.That(german).IsNotEqualTo(tab.LabelKey);
            if (german != tab.Label) translated++;
            var english = SidebarTabFit.Measure(tab.Icon, tab.Label);
            var measured = SidebarTabFit.Measure(tab.Icon, german);
            _output.WriteLine($"{tab.LabelKey}: \"{german}\" box {measured.BorderBox} (English {english.BorderBox})");
            if (!measured.LabelOneLine)
                problems.Add($"{tab.LabelKey}: \"{german}\" wraps or is taller than one line ({FitText.Dip(measured.LabelHeight)})");
            if (Math.Abs(measured.BorderBox.Height + 2 * SettingsWindow.TabButtonMargin - pitch) > 1e-6)
                problems.Add($"{tab.LabelKey}: the tab's pitch changed");
            if (measured.BorderBox != english.BorderBox)
                problems.Add($"{tab.LabelKey}: the tab moved, {english.BorderBox} → {measured.BorderBox}");
        }

        Check.WithCustomMessage("Every tab read its English: the German table is not effective, so nothing was measured.")
            .That(translated).IsStrictlyGreaterThan(10);
        Check.WithCustomMessage("The German labels changed the sidebar:\n  " + string.Join("\n  ", problems)).That(problems).IsEmpty();
    }

    /// <summary>Binding: the pager's row fits in German, and its buttons have German names.</summary>
    [StaFact]
    public void ThePager_InGerman_FitsItsRow_AndNamesItsButtons()
    {
        StringTable.Activate(GermanDraft.Table);
        try
        {
            var indicator = SettingsWindow.BuildPageIndicator(99, 99);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(indicator);
            using var host = FitHost.Open(panel, family: Tokens.FontStandard);
            var indicatorWidth = indicator.ActualWidth + indicator.Margin.Left + indicator.Margin.Right;
            var buttons = 2 * (SettingsWindow.PaginationButtonWidth + 2 * SettingsWindow.PaginationButtonMargin);
            var column = HandChunkedPageFitTests.ContentColumnWidth(SettingsContentFitTests.Xml());
            Check.That(buttons + indicatorWidth).IsLessOrEqualThan(column);

            foreach (var id in new[] { "pag_prev", "pag_next", "tab_prev", "tab_next" })
            {
                foreach (var boundary in new[] { false, true })
                {
                    var key = SettingsPagerChrome.AccessibleNameKey(id, boundary);
                    Check.WithCustomMessage($"{id} (boundary {boundary}) is not German.")
                        .That(StringTable.Current.T(key)).IsNotEqualTo(StringTable.EmbeddedEnglish.T(key));
                }
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// Binding: the drift notice — a crisis surface — shows its whole message. The window is a fixed
    /// 560 DIP, and the message is ONE line (no wrapping) classed <c>row</c>, which no T09 class
    /// measures: a longer translation would be clipped at the banner's edge. English is the
    /// baseline; an over-long message must fail (the teeth).
    /// </summary>
    [StaFact]
    public void TheDriftNotice_ShowsItsWholeMessage_InEnglishAndGerman()
    {
        var english = DriftNotice(StringTable.EmbeddedEnglish.T("drift-notice.message"));
        var german = DriftNotice(GermanDraft.Table.T("drift-notice.message"));
        var overLong = DriftNotice(string.Join(" ", Enumerable.Repeat(GermanDraft.Table.T("drift-notice.message"), 2)));
        _output.WriteLine($"English: {english}");
        _output.WriteLine($"German: {german}");
        Check.WithCustomMessage("The drift-notice gate passed a doubled message: it cannot fail.").That(overLong.Fits).IsFalse();
        Check.WithCustomMessage(english.ToString()).That(english.Fits).IsTrue();
        Check.WithCustomMessage(german.ToString()).That(german.Fits).IsTrue();
    }

    /// <summary>The drift notice's content at 2.0 in its own 560 × 72 window: the message's one line must fit the banner inside it.</summary>
    private static FitVerdict DriftNotice(string message)
    {
        EyeCtrl.App.Tests.Fixtures.WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new DriftNoticeWindow();
        window.Message.Text = message;
        var root = (System.Windows.FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: window.Width, height: window.Height, inheritFrom: window);
        var banner = window.Banner;
        var natural = FitText.NaturalWidth(window.Message, message);
        var room = window.Width - banner.Padding.Left - banner.Padding.Right
                   - banner.BorderThickness.Left - banner.BorderThickness.Right;
        var shown = window.Message.ActualWidth;
        return new FitVerdict(natural <= room && natural <= shown + 0.5,
            $"one line {FitText.Dip(natural)} DIP; shown {FitText.Dip(shown)}; room in the {FitText.Dip(window.Width)} DIP window {FitText.Dip(room)}",
            FitText.ResolvedFace(window.Message), room - natural);
    }

    /// <summary>
    /// Binding (T10 review-01, F1): the calibration window's Retry and Accept buttons, shown after a
    /// poor result, are fixed at 80 and 120 DIP and never wrap, and their keys are classed
    /// <c>row</c>, which only Settings pagination measures — a longer caption is cut at the button's
    /// border. English is the baseline; a doubled caption must fail (the teeth).
    /// </summary>
    [StaFact]
    public void TheCalibrationButtons_ShowTheirWholeCaptions_InEnglishAndGerman()
    {
        var failures = new List<string>();
        foreach (var (language, table) in new[] { ("English", StringTable.EmbeddedEnglish), ("German", GermanDraft.Table) })
        {
            foreach (var (button, key) in CalibrationButtons)
            {
                var verdict = CalibrationButton(button, table.T(key));
                _output.WriteLine($"{language} {key}: {verdict}");
                if (!verdict.Fits) failures.Add($"{language} {key}: {verdict}");
            }
        }

        foreach (var (button, key) in CalibrationButtons)
        {
            var german = GermanDraft.Table.T(key);
            Check.WithCustomMessage($"{key} resolved its English: the German table is not effective.")
                .That(german).IsNotEqualTo(StringTable.EmbeddedEnglish.T(key));
            Check.WithCustomMessage($"The calibration-button gate passed a doubled {key}: it cannot fail.")
                .That(CalibrationButton(button, german + " " + german).Fits).IsFalse();
        }

        Check.WithCustomMessage("The calibration buttons cut their captions:\n  " + string.Join("\n  ", failures))
            .That(failures).IsEmpty();
    }

    /// <summary>
    /// Binding (T10 fixup-01): the calibration window's failure line shows whole, in English and in
    /// German — every <see cref="CalibrationFailureText.Describe"/> sentence, the quality failure for
    /// 1 to 9 failing points. The key is <c>prose</c>, a wrapping class, so the line wraps inside the
    /// window instead of running off both edges, keeps <see cref="CompletionEdgeClearance"/> DIP from
    /// each side (the completion panel's side margin), and the wrapped block reaches neither the title
    /// above it, the Retry/Accept buttons below it nor the cancel hint in the corner. Measured on the
    /// user's screen (1920 × 1200 DIP at 2.0) and on a smaller one, so the wrap width must be the
    /// window's, not a constant; with and without the buttons. One failing point stays on ONE line
    /// on the user's screen (in English, exactly where it was before the line could wrap).
    /// </summary>
    [StaTheory]
    [InlineData("English")]
    [InlineData("German")]
    public void EveryCalibrationFailure_ShowsWhole_InsideTheWindow(string language)
    {
        var table = language == "German" ? GermanDraft.Table : StringTable.EmbeddedEnglish;
        StringTable.Activate(table);
        try
        {
            var problems = new List<string>();
            foreach (var (name, failure) in CalibrationFailures())
            {
                var text = CalibrationFailureText.Describe(failure);
                foreach (var (width, height) in new[] { (1920.0, 1200.0), (1280.0, 800.0) })
                {
                    foreach (var withButtons in new[] { false, true })
                    {
                        var layout = CalibrationFailureLayout(text, width, height, withButtons);
                        if (width == 1920.0 && !withButtons) _output.WriteLine($"{language}, {name}: {layout}");
                        if (!layout.Fits)
                            problems.Add($"{name}, {FitText.Dip(width)} × {FitText.Dip(height)}{(withButtons ? ", with the buttons" : "")}: {layout}");
                    }
                }
            }

            var onePoint = CalibrationFailureLayout(
                CalibrationFailureText.Describe(QualityFailure(1)), 1920.0, 1200.0, withButtons: false);
            Check.WithCustomMessage($"One failing point no longer reads on one line on the user's screen: {onePoint}")
                .That(onePoint.Lines).IsEqualTo(1);
            if (language == "German")
            {
                Check.WithCustomMessage("The failure line resolved its English: the German table is not effective.")
                    .That(CalibrationFailureText.Describe(QualityFailure(1)))
                    .IsNotEqualTo(EnglishOf(() => CalibrationFailureText.Describe(QualityFailure(1))));

                // The teeth: the same block with its wrapping switched off must fail at two points.
                var unwrapped = CalibrationFailureLayout(
                    CalibrationFailureText.Describe(QualityFailure(2)), 1920.0, 1200.0, withButtons: false, noWrap: true);
                Check.WithCustomMessage($"The gate passed two failing points on one unwrapped line: it cannot fail. {unwrapped}")
                    .That(unwrapped.Fits).IsFalse();
            }

            Check.WithCustomMessage($"The calibration failure line is cut or collides ({language}):\n  " + string.Join("\n  ", problems))
                .That(problems).IsEmpty();
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>Every failure the calibration window can show: each code, and the quality failure for 1 to 9 points.</summary>
    private static IEnumerable<(string Name, CalibrationFailure Failure)> CalibrationFailures()
    {
        yield return ("gaze variation", new CalibrationFailure(CalibrationFailureCode.GazeVariation));
        yield return ("unusable transform", new CalibrationFailure(CalibrationFailureCode.UnusableTransform));
        yield return ("distorted transform", new CalibrationFailure(CalibrationFailureCode.DistortedTransform));
        yield return ("target never reached", new CalibrationFailure(CalibrationFailureCode.TargetNeverReached));
        yield return ("target never held, 1 sample", new CalibrationFailure(CalibrationFailureCode.TargetNeverHeld, 120.0, 1));
        yield return ("target never held, 1234 samples", new CalibrationFailure(CalibrationFailureCode.TargetNeverHeld, 120.0, 1234));
        yield return ("unexpected", CalibrationFailure.Unexpected);
        for (var points = 1; points <= 9; points++) yield return ($"quality, {points} point(s)", QualityFailure(points));
    }

    /// <summary>A quality failure at <paramref name="points"/> distinct regions, cycling the three reasons.</summary>
    private static CalibrationFailure QualityFailure(int points)
    {
        var regions = Enum.GetValues<CalibrationTargetRegion>().Where(r => r != CalibrationTargetRegion.Unknown).ToArray();
        (CalibrationQualityReason Reason, double Value, double Limit)[] reasons =
        [
            (CalibrationQualityReason.ExcessScatter, 41.0, 30.0),
            (CalibrationQualityReason.ResidualTooLarge, 140.0, 110.0),
            (CalibrationQualityReason.LowCoverage, 35.0, 60.0),
        ];
        var failures = Enumerable.Range(0, points)
            .Select(i => new CalibrationQualityFailure(i, regions[i], reasons[i % 3].Reason, reasons[i % 3].Value, reasons[i % 3].Limit))
            .ToList();
        return new CalibrationFailure(CalibrationFailureCode.Quality, (IReadOnlyList<CalibrationQualityFailure>)failures);
    }

    /// <summary>Runs <paramref name="describe"/> under the embedded English, then restores the active table.</summary>
    private static string EnglishOf(Func<string> describe)
    {
        var active = StringTable.Current;
        StringTable.ResetToEmbeddedEnglish();
        try
        {
            return describe();
        }
        finally
        {
            StringTable.Activate(active);
        }
    }

    /// <summary>
    /// How far the failure line stays from each side of the window: <c>CompletionCard</c>'s side
    /// margin in <c>CalibrationWindow.xaml</c>, restated here on purpose so the gate pins it.
    /// </summary>
    private const double CompletionEdgeClearance = 40.0;

    /// <summary>The failure line's verdict, its one-line width and how many lines it wraps to.</summary>
    private sealed record FailureLayout(bool Fits, string Detail, double OneLine, int Lines)
    {
        public override string ToString() => $"{(Fits ? "fits" : "FAILS")}: {Detail}";
    }

    /// <summary>
    /// The calibration window as a failed session shows it (<c>ShowCompletion</c>'s presentation,
    /// replayed here because that method shows the window), with <paramref name="detail"/> as the
    /// failure line, hosted at <paramref name="width"/> × <paramref name="height"/> DIP at 2.0.
    /// </summary>
    private static FailureLayout CalibrationFailureLayout(string detail, double width, double height, bool withButtons, bool noWrap = false)
    {
        EyeCtrl.App.Tests.Fixtures.WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new CalibrationWindow();
        var shown = CalibrationCompletion.Describe(measureOnly: false, goodResult: false, errorPx: 0, qualityPercent: 0, errorText: detail);
        window.Backdrop.Visibility = System.Windows.Visibility.Visible;
        window.CompletionCard.Visibility = System.Windows.Visibility.Visible;
        window.CompletionSymbol.Text = shown.Symbol;
        window.CompletionTitle.Text = shown.Title;
        window.CompletionError.Text = shown.Detail;
        window.CompletionHint.Visibility = shown.HintVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        window.RetryPanel.Visibility = shown.RetryVisible || withButtons ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        window.CancelCard.Visibility = shown.CancelHintVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (noWrap) window.CompletionError.TextWrapping = System.Windows.TextWrapping.NoWrap;
        var root = (System.Windows.FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: width, height: height, inheritFrom: window);

        var error = window.CompletionError;
        var screen = new System.Windows.Rect(0, 0, width, height);
        var box = BoundsIn(root, error);
        var oneLine = FitText.NaturalWidth(error, error.Text);
        var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(error);
        var room = Math.Min(slot.Width - error.Margin.Left - error.Margin.Right, width);
        var problems = new List<string>();
        if (!Inside(screen, box)) problems.Add($"its box {Box(box)} leaves the window");

        // The edge clearance, pinned as a number (review-02, C4): read from the panel's Margin it
        // would follow that margin to zero, and a line could run to the screen's edge unnoticed.
        var clear = new System.Windows.Rect(CompletionEdgeClearance, 0, width - 2 * CompletionEdgeClearance, height);
        if (!Inside(clear, box))
            problems.Add($"its box {Box(box)} comes within {FitText.Dip(CompletionEdgeClearance)} DIP of the window's side");
        if (error.TextWrapping == System.Windows.TextWrapping.NoWrap)
        {
            if (oneLine > Math.Min(error.ActualWidth, room) + 0.5) problems.Add($"its one line is cut at {FitText.Dip(room)} DIP");
        }
        else
        {
            var (run, runWidth) = FitText.WidestRun(error, error.Text);
            if (runWidth > room + 0.5) problems.Add($"the run '{run}' ({FitText.Dip(runWidth)} DIP) cannot break in {FitText.Dip(room)}");
        }

        foreach (var (name, other) in new (string, System.Windows.FrameworkElement)[]
                 {
                     ("the title", window.CompletionTitle), ("the hint", window.CompletionHint),
                     ("the Retry/Accept buttons", window.RetryPanel), ("the cancel hint", window.CancelHint),
                 })
        {
            if (!other.IsVisible) continue;
            var otherBox = BoundsIn(root, other);
            var overlap = System.Windows.Rect.Intersect(box, otherBox);
            if (!overlap.IsEmpty && overlap.Width > 0.01 && overlap.Height > 0.01) problems.Add($"it overlaps {name} {Box(otherBox)}");
            if (!Inside(screen, otherBox)) problems.Add($"{name} {Box(otherBox)} is pushed out of the window");
        }

        var lines = (int)Math.Round(error.ActualHeight / LineHeight(error));
        return new FailureLayout(problems.Count == 0,
            $"{FitText.Dip(oneLine)} DIP on one line; {lines} line(s) in {Box(box)}"
            + (problems.Count > 0 ? "; " + string.Join("; ", problems) : ""),
            oneLine, lines);
    }

    /// <summary>The height of one line of <paramref name="like"/>'s text.</summary>
    private static double LineHeight(TextBlock like)
    {
        var probe = new TextBlock { Text = "Ag", FontFamily = like.FontFamily, FontSize = like.FontSize, FontWeight = like.FontWeight };
        probe.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }

    private static System.Windows.Rect BoundsIn(System.Windows.Media.Visual root, System.Windows.FrameworkElement element) =>
        element.TransformToAncestor(root).TransformBounds(new System.Windows.Rect(element.RenderSize));

    private static bool Inside(System.Windows.Rect outer, System.Windows.Rect inner) =>
        inner.Left >= outer.Left - 0.5 && inner.Top >= outer.Top - 0.5
        && inner.Right <= outer.Right + 0.5 && inner.Bottom <= outer.Bottom + 0.5;

    private static string Box(System.Windows.Rect r) =>
        $"[{FitText.Dip(r.Left)}, {FitText.Dip(r.Top)} – {FitText.Dip(r.Right)}, {FitText.Dip(r.Bottom)}]";

    /// <summary>The two buttons of the poor-result panel and their keys.</summary>
    private static readonly (Func<CalibrationWindow, Button> Button, string Key)[] CalibrationButtons =
        [(w => w.RetryButton, "calibration.window.retry"), (w => w.AcceptButton, "calibration.window.accept")];

    /// <summary>
    /// One calibration button's caption at 2.0 in the real window: the poor-result panel, hosted at
    /// the screen's size with the window's own font. The room is what the button's template gives
    /// its content — the presenter's slot less its margin, i.e. the fixed width less border and
    /// padding — never the text's <c>ActualWidth</c>, which a clipped one-line text reports unclipped.
    /// </summary>
    private static FitVerdict CalibrationButton(Func<CalibrationWindow, Button> pick, string caption)
    {
        EyeCtrl.App.Tests.Fixtures.WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new CalibrationWindow();
        window.CompletionCard.Visibility = System.Windows.Visibility.Visible;
        window.RetryPanel.Visibility = System.Windows.Visibility.Visible;
        var button = pick(window);
        button.Content = caption;
        var root = (System.Windows.FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: 1920, height: 1200, inheritFrom: window);
        var presenter = FitText.Descendants<ContentPresenter>(button).Single();
        var text = FitText.Descendants<TextBlock>(presenter).Single();
        var natural = FitText.NaturalWidth(text, caption);
        var room = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(presenter).Width
                   - presenter.Margin.Left - presenter.Margin.Right;
        return new FitVerdict(natural <= room,
            $"\"{caption}\" one line {FitText.Dip(natural)} DIP; room in the {FitText.Dip(button.ActualWidth)} DIP button {FitText.Dip(room)}",
            FitText.ResolvedFace(text), room - natural);
    }

    /// <summary>Binding: every German TrackStatus row fits the fixed window's width (de-DE digits included).</summary>
    [StaFact]
    public void EveryTrackStatusRow_InGerman_FitsTheWindowWidth()
    {
        var german = GermanDraft.Table;
        Check.WithCustomMessage("The TrackStatus title resolved its English: the German table is not effective.")
            .That(german.T("track-status.title")).IsNotEqualTo(StringTable.EmbeddedEnglish.T("track-status.title"));
        var failures = new List<string>();
        foreach (var quality in Enum.GetValues<DistanceQuality>())
        {
            var verdict = TrackStatusWidthTests.Measure(
                german.T("track-status.title"),
                german.T("track-status.distance"),
                german.T("track-status.distance.mm", 1000.0),
                german.T("track-status.quality"),
                TrackStatusWindow.QualityWord(german, quality));
            _output.WriteLine($"{quality}: {verdict}");
            if (!verdict.Fits) failures.Add($"{quality}: {verdict}");
        }

        Check.WithCustomMessage("German TrackStatus rows overrun the window width:\n  " + string.Join("\n  ", failures))
            .That(failures).IsEmpty();
    }
}
