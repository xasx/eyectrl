namespace EyeCtrl.Supervisor.Tests.UI;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Ipc;
using EyeCtrl.Supervisor.Supervision;
using EyeCtrl.Supervisor.UI;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T09 PR 1, the Supervisor's fit classes — the only project that can build its windows:
/// <c>tray63</c>, <c>B260x3</c> (the FallenBack buttons), <c>C-fallback</c> (the FallenBack
/// window's class-C growth), and the setup window's surface gate (no clipped label, toggles keep
/// their size, every row reachable in the ScrollViewer). App.Tests'
/// <c>FitClassTests.EveryFitClass_HasAMeasurer</c> declares these three classes as measured here.
///
/// <para>
/// Every window is built on an STA thread and NEVER shown: its content is detached into the
/// shared <see cref="FitHost"/> at 2.0. English is the baseline (no exceptions are recorded for
/// these classes); each class also has a teeth test — an over-long text must fail it.
/// </para>
/// </summary>
[Collection(SupervisorXamlCollection.Name)]
public sealed class SupervisorFitTests : IDisposable
{
    /// <summary>The 200 % work area less a 48 DIP taskbar: a grown window must fit it.</summary>
    private const double WorkAreaHeight = 1152;

    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eyectrl-t09-fit-" + Guid.NewGuid().ToString("N"));

    public SupervisorFitTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static IEnumerable<(string Name, string Text)> OverLongTexts()
    {
        yield return ("unbreakable", new string('W', 120));
        yield return ("spaced", string.Join(" ", Enumerable.Repeat("Wwww", 2000)));
    }

    // ────── tray63 ──────

    /// <summary>The tray tooltip for one phrase: the production prefix + phrase, against the shell cap.</summary>
    private static FitVerdict Tray(string prefix, string phrase)
    {
        var text = prefix + phrase;
        return new FitVerdict(text.Length <= SupervisorTrayIcon.TooltipCharacterCap,
            $"\"{text}\" is {text.Length} characters vs {SupervisorTrayIcon.TooltipCharacterCap}", "<characters>");
    }

    [Fact]
    public void Tray63_EveryEnglishKey_FitsTheTooltip()
    {
        var english = StringTable.EmbeddedEnglish;
        var prefix = SupervisorStateText.TrayPrefix(english);
        var phrases = FitCatalog.ItemsOf("tray63").Where(i => i.Key != "supervisor.tray.prefix").ToList();
        Check.WithCustomMessage("No tray63 phrase keys: the gate would measure nothing.").That(phrases).Not.IsEmpty();

        // Every phrase with the prefix, and the prefix with the longest phrase (the prefix key's own check).
        var failures = phrases.Select(p => (p.Key, Verdict: Tray(prefix, p.Text)))
            .Append(("supervisor.tray.prefix", Tray(prefix, phrases.MaxBy(p => p.Text.Length)!.Text)))
            .Where(r => { _output.WriteLine($"{r.Item1}: {r.Item2}"); return !r.Item2.Fits; })
            .Select(r => $"{r.Item1}: {r.Item2}")
            .ToList();

        Check.WithCustomMessage("Tray tooltips over the shell cap:\n  " + string.Join("\n  ", failures)).That(failures).IsEmpty();
    }

    [Fact]
    public void Tray63_AnOverLongPhrase_Fails()
    {
        var prefix = SupervisorStateText.TrayPrefix(StringTable.EmbeddedEnglish);
        Check.That(Tray(prefix, "Die Augensteuerung wurde beendet — das Ersatzprogramm läuft jetzt").Fits).IsFalse();
    }

    // ────── B260x3 and C-fallback ──────

    /// <summary>
    /// The FallenBack window's chrome at this machine's metrics: a captioned, bordered window. The
    /// resizable frame's thickness is used even though the dialog is NoResize — it is the larger,
    /// so the client area measured is never more generous than the real one.
    /// </summary>
    private static Thickness Chrome() => SystemParameters.WindowNonClientFrameThickness;

    /// <summary>
    /// C-fallback: the dialog's content laid out at 2.0 in its client width, with the client
    /// height the window's own sizing rule produces — <c>SizeToContent=Height</c> from its
    /// <c>MinHeight</c>, or its fixed <c>Height</c> if the XAML ever loses the growth. The button
    /// row must keep its full 110 DIP buttons, the grown window must fit the work area, and every
    /// run of the title and body must fit the text width.
    /// </summary>
    private static FitVerdict FallenBack(string? title, string? body, string? buttonLabel)
    {
        var dialog = new FallenBackDialog(StringTable.EmbeddedEnglish);
        try
        {
            if (title is not null) dialog.HeadlineText.Text = title;
            if (body is not null) dialog.BodyText.Text = body;
            var buttons = dialog.ButtonHost.Children.OfType<Border>().ToList();
            var label = FitText.Descendants<TextBlock>(buttons[0]).First(t => t.Text.Length > 0);
            if (buttonLabel is not null) label.Text = buttonLabel;

            var chrome = Chrome();
            var clientWidth = dialog.Width - chrome.Left - chrome.Right;
            var root = (FrameworkElement)dialog.Content;
            dialog.Content = null;

            double desired;
            using (var probe = FitHost.Open(root, width: clientWidth, inheritFrom: dialog))
                desired = FitText.NaturalHeight(probe, root, clientWidth - root.Margin.Left - root.Margin.Right)
                          + root.Margin.Top + root.Margin.Bottom;

            var minClient = dialog.MinHeight - chrome.Top - chrome.Bottom;
            var clientHeight = dialog.SizeToContent == SizeToContent.Height
                ? Math.Max(minClient, desired)
                : dialog.Height - chrome.Top - chrome.Bottom;
            var outer = clientHeight + chrome.Top + chrome.Bottom;

            using var host = FitHost.Open(root, width: clientWidth, height: clientHeight, inheritFrom: dialog);
            var buttonHeights = buttons.Select(b => b.ActualHeight).ToList();
            var fullButtons = buttonHeights.All(h => h >= 110 - 1e-6);

            var textWidth = dialog.HeadlineText.ActualWidth;
            var (titleRun, titleRunWidth) = FitText.WidestRun(dialog.HeadlineText, dialog.HeadlineText.Text);
            var (bodyRun, bodyRunWidth) = FitText.WidestRun(dialog.BodyText, dialog.BodyText.Text);

            // B260x3: the button's own label in its 260 × (110 − 6) room.
            var layout = (Grid)label.Parent;
            var (buttonRun, buttonRunWidth) = FitText.WidestRun(label, label.Text);
            var labelHeight = FitText.NaturalHeight(host, label, layout.ActualWidth);
            var buttonFits = buttonRunWidth <= layout.ActualWidth && labelHeight <= layout.RowDefinitions[0].ActualHeight;

            var ok = fullButtons && outer <= WorkAreaHeight && titleRunWidth <= textWidth && bodyRunWidth <= textWidth && buttonFits;
            return new FitVerdict(ok,
                $"content {FitText.Dip(desired)} → client {FitText.Dip(clientWidth)}×{FitText.Dip(clientHeight)} "
                + $"(min {FitText.Dip(minClient)}), window {FitText.Dip(outer)} vs work area {WorkAreaHeight}; buttons "
                + $"{string.Join("/", buttonHeights.Select(FitText.Dip))} (need 110); title run '{titleRun}' "
                + $"{FitText.Dip(titleRunWidth)} and body run '{bodyRun}' {FitText.Dip(bodyRunWidth)} vs {FitText.Dip(textWidth)}; "
                + $"button run '{buttonRun}' {FitText.Dip(buttonRunWidth)} vs {FitText.Dip(layout.ActualWidth)}, label "
                + $"{FitText.Dip(labelHeight)} vs {FitText.Dip(layout.RowDefinitions[0].ActualHeight)}",
                FitText.ResolvedFace(buttonLabel is not null ? label : body is not null ? dialog.BodyText : dialog.HeadlineText));
        }
        finally
        {
            dialog.Close();
        }
    }

    [Fact]
    public void CFallback_TheGrowthIsDeclared_HeightOnly()
    {
        RunSta(() =>
        {
            var dialog = new FallenBackDialog(StringTable.EmbeddedEnglish);
            try
            {
                Check.That(dialog.SizeToContent).IsEqualTo(SizeToContent.Height);
                Check.That(dialog.MinHeight).IsEqualTo(320.0);
                Check.WithCustomMessage("A fixed Height would cap the growth; the XAML must leave it to SizeToContent.")
                    .That(double.IsNaN(dialog.Height)).IsTrue();
                Check.That(dialog.Width).IsEqualTo(640.0);
                Check.That(dialog.ResizeMode).IsEqualTo(ResizeMode.NoResize);
                Check.That(dialog.WindowStartupLocation).IsEqualTo(WindowStartupLocation.CenterScreen);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void CFallback_AndB260x3_TheEnglishFits_AndKeepsTodaysWindow()
    {
        RunSta(() =>
        {
            var verdict = FallenBack(null, null, null);
            _output.WriteLine(verdict.ToString());
            Check.WithCustomMessage(verdict.ToString()).That(verdict.Fits).IsTrue();

            // English must not grow the window: its content fits today's 320.
            Check.WithCustomMessage("English grew the FallenBack dialog past 320: " + verdict)
                .That(verdict.Detail).Contains($"window {FitText.Dip(320)} ");
        });
    }

    [Fact]
    public void B260x3_EveryEnglishButtonLabel_Fits()
    {
        RunSta(() =>
        {
            var items = FitCatalog.ItemsOf("B260x3");
            Check.That(items).Not.IsEmpty();
            foreach (var item in items)
            {
                var verdict = FallenBack(null, null, item.Text);
                _output.WriteLine($"{item}: {verdict}");
                Check.WithCustomMessage($"{item}: {verdict}").That(verdict.Fits).IsTrue();
            }
        });
    }

    [Fact]
    public void CFallback_EveryEnglishText_Fits()
    {
        RunSta(() =>
        {
            var items = FitCatalog.ItemsOf("C-fallback");
            Check.That(items).Not.IsEmpty();
            foreach (var item in items)
            {
                var isTitle = item.Key == "supervisor.fallback.title";
                var verdict = FallenBack(isTitle ? item.Text : null, isTitle ? null : item.Text, null);
                _output.WriteLine($"{item}: {verdict}");
                Check.WithCustomMessage($"{item}: {verdict}").That(verdict.Fits).IsTrue();
            }
        });
    }

    [Fact]
    public void CFallback_ALongerBody_GrowsTheWindow_AndTheButtonsKeepTheirHeight()
    {
        RunSta(() =>
        {
            var body = string.Join(" ", Enumerable.Repeat(
                "Eine Ersatz-Augensteuerung wurde gestartet. Wählen Sie damit (oder mit der Maus) unten eine Option.", 3));
            var verdict = FallenBack(null, body, null);
            _output.WriteLine(verdict.ToString());
            Check.WithCustomMessage(verdict.ToString()).That(verdict.Fits).IsTrue();
            Check.WithCustomMessage("A three-times-longer body must grow the window past 320: " + verdict)
                .That(verdict.Detail).Not.Contains($"window {FitText.Dip(320)} ");
        });
    }

    [Fact]
    public void BothClasses_AnOverLongText_Fails()
    {
        RunSta(() =>
        {
            var survivors = new List<string>();
            foreach (var (name, text) in OverLongTexts())
            {
                foreach (var (slot, verdict) in new[]
                         {
                             ("title", FallenBack(text, null, null)),
                             ("body", FallenBack(null, text, null)),
                             ("button", FallenBack(null, null, text)),
                         })
                {
                    _output.WriteLine($"{slot} {name}: {verdict}");
                    if (verdict.Fits) survivors.Add($"{slot} {name}: {verdict}");
                }
            }

            Check.WithCustomMessage("An over-long text passed: the gate is vacuous.\n  " + string.Join("\n  ", survivors))
                .That(survivors).IsEmpty();
        });
    }

    // ────── The setup window ──────

    private SupervisorSetupWindow BuildSetupWindow(StringTable strings)
    {
        var language = new SettingDelivery<UiLanguage>(
            "UiLanguage",
            (value, restart) => SettingRequest.SetUiLanguage(value, restart),
            _ => new TaskCompletionSource<AppRequestResult>().Task,
            work => work(),
            readDisk: () => UiLanguage.System,
            runInBackground: work => work(),
            utcNow: () => DateTime.UtcNow,
            confirmSaveByReRead: true);
        var startOnBoot = new SettingDelivery<bool>(
            "StartOnBoot", (value, _) => SettingRequest.SetStartOnBoot(value),
            _ => new TaskCompletionSource<AppRequestResult>().Task,
            work => work());
        return new SupervisorSetupWindow(
            new EyeCtrlSettings(),
            new SettingsService(Path.Combine(_root, "settings.json"), SettingsWriter.Supervisor),
            _ => { },
            () => { },
            new StartOnBootSetupRow(startOnBoot, () => false),
            new LanguageSetupRow(language, () => UiLanguage.System, CultureInfo.GetCultureInfo("de-DE")),
            strings,
            strings.T("supervisor.setup.status",
                SupervisorStateText.Describe(strings, Enum.GetValues<SupervisorState>().First())));
    }

    /// <summary>
    /// The setup gate's findings in two kinds: REACH (a dwell control outside the viewport's width
    /// or the scroll extent, a switch that lost its 160 × 100 box, a last row the scroller cannot
    /// bring into view) — binding in every language — and CLIP (a text that does not show all of
    /// itself) — binding in English, reported for the pseudo-locale.
    /// </summary>
    private sealed record SetupResult(FitVerdict Verdict, IReadOnlyList<string> Reach, IReadOnlyList<string> Clip);

    /// <summary>
    /// The setup window's surface gate, at 2.0 in its 560 × 640 window (less the chrome), with the
    /// Advanced expander open so its rows are laid out too: every visible TextBlock shows all of its
    /// text (a one-line text is as wide as its natural width and ends inside the viewport; a wrapping
    /// one is as tall as its full wrap, so no MaxHeight trims it, and no run breaks mid-word); both
    /// toggles' switches keep their 160 × 100 box; and scrolling to the end brings the last row into
    /// the viewport.
    /// </summary>
    private FitVerdict Setup(Action<SupervisorSetupWindow>? edit = null) => SetupIn(StringTable.EmbeddedEnglish, edit).Verdict;

    private SetupResult SetupIn(StringTable strings, Action<SupervisorSetupWindow>? edit = null)
    {
        var window = BuildSetupWindow(strings);
        try
        {
            window.AdvancedExpander.IsExpanded = true;
            edit?.Invoke(window);

            var chrome = Chrome();
            var clientWidth = window.Width - chrome.Left - chrome.Right;
            var clientHeight = window.Height - chrome.Top - chrome.Bottom;
            var scroller = (ScrollViewer)window.Content;
            window.Content = null;
            using var host = FitHost.Open(scroller, width: clientWidth, height: clientHeight, inheritFrom: window);

            var problems = new List<string>();
            var reach = new List<string>();
            var viewportRight = scroller.ViewportWidth;
            var measured = 0;
            foreach (var text in FitText.Descendants<TextBlock>(scroller).Where(t => t.IsVisible && t.Text.Length > 0).ToList())
            {
                measured++;
                var left = text.TransformToAncestor(scroller).Transform(new Point(0, 0)).X;
                if (text.TextWrapping == TextWrapping.NoWrap)
                {
                    var natural = FitText.NaturalWidth(text, text.Text) + text.Padding.Left + text.Padding.Right;
                    if (natural > text.ActualWidth + 0.5)
                        problems.Add($"'{text.Text}' needs {FitText.Dip(natural)} but has {FitText.Dip(text.ActualWidth)} (clipped)");
                    if (left + natural > viewportRight + 0.5)
                        problems.Add($"'{text.Text}' ends at {FitText.Dip(left + natural)}, past the viewport {FitText.Dip(viewportRight)}");
                    continue;
                }

                // The width the parent offers (the layout slot less the margins), not ActualWidth:
                // a wrapped text narrower than its slot reports the width of its longest line.
                var slot = LayoutInformation.GetLayoutSlot(text);
                var width = slot.Width - text.Margin.Left - text.Margin.Right;
                var maxHeight = text.MaxHeight;
                text.MaxHeight = double.PositiveInfinity;
                var full = FitText.NaturalHeight(host, text, width);
                text.MaxHeight = maxHeight;
                host.Relayout();
                if (full > Math.Min(maxHeight, text.ActualHeight) + 0.5)
                    problems.Add($"'{text.Text}' wraps to {FitText.Dip(full)} but shows {FitText.Dip(Math.Min(maxHeight, text.ActualHeight))} (trimmed)");
                var (run, runWidth) = FitText.WidestRun(text, text.Text);
                if (runWidth > width + 0.5)
                    problems.Add($"'{text.Text}': run '{run}' {FitText.Dip(runWidth)} breaks mid-word in {FitText.Dip(width)}");
            }

            foreach (var id in new[] { "setup_enable_supervision", "setup_start_on_logon" })
            {
                var word = FitText.Descendants<TextBlock>(scroller)
                    .Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == $"Settings_Toggle_{id}");
                DependencyObject node = word;
                while (node is not Border { Width: 160 } && VisualTreeHelper.GetParent(node) is { } parent) node = parent;
                if (node is not Border sw) { reach.Add($"{id}: no 160 DIP switch found"); continue; }
                if (Math.Abs(sw.ActualWidth - 160) > 1e-6 || Math.Abs(sw.ActualHeight - sw.Height) > 1e-6)
                    reach.Add($"{id}: the switch is {FitText.Dip(sw.ActualWidth)}×{FitText.Dip(sw.ActualHeight)}, not 160×{FitText.Dip(sw.Height)}");
            }

            // Every dwell control — a DwellButton root carries the hand cursor — lies inside the
            // viewport's width and inside the scroll extent, so the scroller can bring it into view.
            var controls = FitText.Descendants<Border>(scroller).Where(b => b.Cursor == System.Windows.Input.Cursors.Hand).ToList();
            if (controls.Count < 5) throw new InvalidOperationException($"Only {controls.Count} dwell controls found: the reach check is not seeing the window.");
            foreach (var control in controls)
            {
                var box = control.TransformToAncestor((Visual)scroller.Content).TransformBounds(new Rect(control.RenderSize));
                if (box.Left < -0.5 || box.Right > viewportRight + 0.5 || box.Bottom > scroller.ExtentHeight + 0.5)
                    reach.Add($"a dwell control at {box} lies outside the {FitText.Dip(viewportRight)} viewport / {FitText.Dip(scroller.ExtentHeight)} extent");
            }

            scroller.ScrollToEnd();
            host.Relayout();
            var last = window.RootPanel.Children.OfType<FrameworkElement>().Last(e => e.IsVisible);
            var lastBottom = last.TransformToAncestor(scroller).Transform(new Point(0, last.ActualHeight)).Y;
            if (lastBottom > scroller.ViewportHeight + 0.5)
                reach.Add($"scrolled to the end, the last row ends at {FitText.Dip(lastBottom)}, below the viewport {FitText.Dip(scroller.ViewportHeight)}");

            if (measured < 10) throw new InvalidOperationException($"Only {measured} texts measured: the setup gate is not seeing the window.");
            var all = reach.Concat(problems).ToList();
            var verdict = new FitVerdict(all.Count == 0,
                $"{measured} texts and {controls.Count} dwell controls in a {FitText.Dip(clientWidth)}×{FitText.Dip(clientHeight)} "
                + $"client, viewport {FitText.Dip(scroller.ViewportWidth)}×{FitText.Dip(scroller.ViewportHeight)}, extent "
                + $"{FitText.Dip(scroller.ExtentHeight)}"
                + (all.Count == 0 ? "" : ":\n  " + string.Join("\n  ", all)),
                FitText.ResolvedFace(window.SubtitleText));
            return new SetupResult(verdict, reach, problems);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Setup_TheEnglishWindow_ClipsNothing_KeepsItsToggles_AndScrollsToEveryRow()
    {
        RunSta(() =>
        {
            var verdict = Setup();
            _output.WriteLine(verdict.ToString());
            Check.WithCustomMessage(verdict.ToString()).That(verdict.Fits).IsTrue();
        });
    }

    /// <summary>
    /// T09 PR 2, binding: under the pseudo-locale the setup window still reaches every row — every
    /// dwell control inside the viewport's width and the scroll extent, both switches at 160 × 100,
    /// and scrolling to the end shows the last row. Texts that no longer show whole are a FIT
    /// finding, reported by <see cref="PseudoLocaleFitReport_IsWritten_ForEveryKey"/>.
    /// </summary>
    [Fact]
    public void Setup_UnderThePseudoLocale_EveryRowStaysReachable()
    {
        RunSta(() =>
        {
            var result = SetupIn(PseudoLocale.Table);
            _output.WriteLine(result.Verdict.ToString());
            Check.That(PseudoLocale.Table.T("supervisor.setup.window-title")).StartsWith("[");
            Check.WithCustomMessage("Under the pseudo-locale the setup window loses a row:\n  " + string.Join("\n  ", result.Reach))
                .That(result.Reach).IsEmpty();
        });
    }

    /// <summary>
    /// T09 PR 2, NON-binding: the Supervisor's fit classes (tray63, B260x3, C-fallback) and the
    /// setup window's text findings under the pseudo-locale, written to
    /// <c>t09-fit-report/pseudo-locale-supervisor.md</c> in the test output as an early warning for
    /// T10. The Supervisor's windows carry no theme of their own, so one pass covers them. Asserts
    /// only that every key was measured.
    /// </summary>
    [Fact]
    public void PseudoLocaleFitReport_IsWritten_ForEveryKey()
    {
        RunSta(() =>
        {
            var rows = new List<FitReportRow>();
            var pseudo = PseudoLocale.Table;
            var prefix = SupervisorStateText.TrayPrefix(pseudo);
            foreach (var item in PseudoLocale.ItemsOf("tray63"))
            {
                var phrase = item.Key == "supervisor.tray.prefix"
                    ? PseudoLocale.ItemsOf("tray63").Where(i => i.Key != item.Key).MaxBy(i => i.Text.Length)!.Text
                    : item.Text;
                rows.Add(new FitReportRow("-", "tray63", item.Key, prefix + phrase, Tray(prefix, phrase)));
            }

            foreach (var item in PseudoLocale.ItemsOf("B260x3"))
                rows.Add(new FitReportRow("-", "B260x3", item.Key, item.Text, FallenBack(null, null, item.Text)));
            foreach (var item in PseudoLocale.ItemsOf("C-fallback"))
            {
                var isTitle = item.Key == "supervisor.fallback.title";
                rows.Add(new FitReportRow("-", "C-fallback", item.Key, item.Text,
                    FallenBack(isTitle ? item.Text : null, isTitle ? null : item.Text, null)));
            }

            var setup = SetupIn(pseudo);
            var notes = setup.Clip.Select(c => "Supervisor setup window: " + c)
                .DefaultIfEmpty("Supervisor setup window: every text shows whole under the pseudo-locale.")
                .ToList();
            var path = FitReport.Write("pseudo-locale-supervisor.md", "T09 pseudo-locale fit report — Supervisor", rows, notes);
            _output.WriteLine($"Report: {path} ({rows.Count(r => !r.Verdict.Fits)} of {rows.Count} do not fit; "
                              + $"{setup.Clip.Count} setup-window text findings)");

            var expected = new[] { "tray63", "B260x3", "C-fallback" }.Sum(c => FitCatalog.ItemsOf(c).Count);
            Check.That(rows).HasSize(expected);
            Check.That(File.Exists(path)).IsTrue();
        });
    }

    [Fact]
    public void Setup_AnOverLongText_Fails()
    {
        RunSta(() =>
        {
            var survivors = new List<string>();
            foreach (var (name, text) in OverLongTexts())
            {
                foreach (var (slot, edit) in new (string, Action<SupervisorSetupWindow>)[]
                         {
                             ("heading (one line)", w => w.FallbackHeadingText.Text = text),
                             ("toggle label (wraps, MaxHeight, ellipsis)", w => FitText.Descendants<TextBlock>(w.ToggleHost)
                                 .First(t => t.Text == "Enable supervision").Text = text),
                             ("advanced label (one line)", w => w.MaxRetriesLabel.Text = text),
                         })
                {
                    var verdict = Setup(edit);
                    _output.WriteLine($"{slot} {name}: {verdict}");
                    if (verdict.Fits) survivors.Add($"{slot} {name}");
                }
            }

            Check.WithCustomMessage("An over-long text passed the setup gate:\n  " + string.Join("\n  ", survivors))
                .That(survivors).IsEmpty();
        });
    }
}
