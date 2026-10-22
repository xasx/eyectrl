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
using System.Windows.Media.Imaging;
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
    /// run of the title and body must fit the text width. <paramref name="showCountdown"/> adds A3's
    /// auto-retry countdown line (the dialog as it opens on the session's first episode) - the
    /// floor pins below measure the dialog without it, because a window ABOVE the 320 floor is
    /// content + machine chrome and no number of it pins across machines.
    /// </summary>
    private static FitVerdict FallenBack(string? title, string? body, string? buttonLabel, StringTable? strings = null, bool showCountdown = false)
    {
        var dialog = new FallenBackDialog(strings ?? StringTable.EmbeddedEnglish);
        try
        {
            if (showCountdown) dialog.ShowAutoRetryCountdown();
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
            var (countdownRun, countdownRunWidth) = FitText.WidestRun(dialog.CountdownText, dialog.CountdownText.Text);

            // B260x3: the button's own label in its 260 × (110 − 6) room.
            var layout = (Grid)label.Parent;
            var (buttonRun, buttonRunWidth) = FitText.WidestRun(label, label.Text);
            var labelHeight = FitText.NaturalHeight(host, label, layout.ActualWidth);
            var buttonFits = buttonRunWidth <= layout.ActualWidth && labelHeight <= layout.RowDefinitions[0].ActualHeight;

            var ok = fullButtons && outer <= WorkAreaHeight && titleRunWidth <= textWidth && bodyRunWidth <= textWidth
                     && countdownRunWidth <= textWidth && buttonFits;
            return new FitVerdict(ok,
                $"content {FitText.Dip(desired)} → client {FitText.Dip(clientWidth)}×{FitText.Dip(clientHeight)} "
                + $"(min {FitText.Dip(minClient)}), window {FitText.Dip(outer)} vs work area {WorkAreaHeight}; buttons "
                + $"{string.Join("/", buttonHeights.Select(FitText.Dip))} (need 110); title run '{titleRun}' "
                + $"{FitText.Dip(titleRunWidth)}, body run '{bodyRun}' {FitText.Dip(bodyRunWidth)} and countdown run "
                + $"'{countdownRun}' {FitText.Dip(countdownRunWidth)} vs {FitText.Dip(textWidth)}; "
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

    /// <summary>
    /// A3: the dialog as it opens on the session's first episode carries the auto-retry countdown
    /// line, and that line grows the window past the 320 DIP floor in BOTH languages (T09's growth:
    /// the height follows the content instead of squeezing the button row). The grown state is
    /// examined here - full buttons, the work area, no broken run - and the two languages are
    /// compared rather than pinned to an absolute: above the floor the window is content + machine
    /// chrome, and no absolute of it holds on another machine (CI's frame is wider than this one's).
    /// The floor pin above keeps holding the floor for the dialog WITHOUT the countdown (a second
    /// episode, or a countdown already fired or cancelled).
    /// </summary>
    [Fact]
    public void CFallback_WithTheAutoRetryCountdown_GrowsTheWindowAndStillFits()
    {
        RunSta(() =>
        {
            var english = FallenBack(null, null, null, showCountdown: true);
            var german = FallenBack(null, null, null, GermanDraft.Table, showCountdown: true);
            _output.WriteLine("the English dialog as it opens: " + english);
            _output.WriteLine("the German dialog as it opens: " + german);

            Check.WithCustomMessage(english.ToString()).That(english.Fits).IsTrue();
            Check.WithCustomMessage(german.ToString()).That(german.Fits).IsTrue();

            // The countdown line is content: the window leaves the floor rather than squeezing it
            // away. (Its WIDTH is gated like every C-fallback text - CFallback_EveryEnglishText_Fits
            // measures it at the body's face.)
            Check.WithCustomMessage("the countdown line was not laid out: " + english)
                .That(english.Detail).Not.Contains($"window {FitText.Dip(320)} ");
            Check.WithCustomMessage("the countdown line was not laid out: " + german)
                .That(german.Detail).Not.Contains($"window {FitText.Dip(320)} ");

            // German must grow the dialog exactly as English does: one countdown line, one window.
            // If a machine's metrics wrap the German body to a further line, this is the re-measure
            // the floor pin's comment asks for.
            Check.WithCustomMessage(
                "German grows the dialog differently from English; re-measure both:\n  " + english + "\n  " + german)
                .That(WindowFigure(german.Detail)).IsEqualTo(WindowFigure(english.Detail));
        });
    }

    /// <summary>The "window X " figure of a verdict's detail, for comparing two grown dialogs.</summary>
    private static string WindowFigure(string detail) =>
        System.Text.RegularExpressions.Regex.Match(detail, @"window [^ ]+ ").Value;

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
    /// and scrolling to the end shows the last row — AND every text still shows whole (review-02
    /// R3: the CLIP findings were already empty under the pseudo-locale, so they bind now rather than
    /// stay a note). T10 runs the same gate on its German.
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
            Check.WithCustomMessage("Under the pseudo-locale a setup-window text no longer shows whole:\n  " + string.Join("\n  ", result.Clip))
                .That(result.Clip).IsEmpty();
        });
    }

    // ────── T10: the user's review sheets (not a gate; EYECTRL_WRITE_T10_REVIEW=1) ──────

    /// <summary>
    /// The Supervisor's pieces of T10's review sheets, in English and German at 2.0: the FallenBack
    /// dialog as it opens (its client area at the height the window would take), and the whole
    /// setup form (Advanced open, laid out at full height). Written as PNG fragments that
    /// App.Tests' sheet composer places next to each other. Writes nothing unless asked.
    /// </summary>
    [Fact]
    public void ReviewFragments_WhenAsked()
    {
        Assert.SkipUnless(ReviewRender.Requested, "set EYECTRL_WRITE_T10_REVIEW=1 to write the T10 review fragments");
        RunSta(() =>
        {
            foreach (var (language, strings) in new[] { ("en", StringTable.EmbeddedEnglish), ("de", GermanDraft.Table) })
            {
                ReviewRender.Save(FallenBackImage(strings), ReviewRender.FragmentPath($"fallenback-{language}"));
                var form = FormLayout(strings, leftAlignedCards: false, render: true);
                ReviewRender.Save((BitmapSource)form.Image!, ReviewRender.FragmentPath($"setup-{language}"));
            }
        });
    }

    /// <summary>The FallenBack dialog's client area in <paramref name="strings"/>, sized as the window sizes itself.</summary>
    private static BitmapSource FallenBackImage(StringTable strings)
    {
        var dialog = new FallenBackDialog(strings);
        try
        {
            dialog.ShowAutoRetryCountdown();
            var chrome = Chrome();
            var clientWidth = dialog.Width - chrome.Left - chrome.Right;
            var root = (FrameworkElement)dialog.Content;
            dialog.Content = null;
            double desired;
            using (var probe = FitHost.Open(root, width: clientWidth, inheritFrom: dialog))
                desired = FitText.NaturalHeight(probe, root, clientWidth - root.Margin.Left - root.Margin.Right)
                          + root.Margin.Top + root.Margin.Bottom;
            var clientHeight = Math.Max(dialog.MinHeight - chrome.Top - chrome.Bottom, desired);
            // The whole client area, its margins included: the root draws inside them.
            var client = new Border { Background = dialog.Background, Child = root };
            return ReviewRender.Snap(client, clientWidth, clientHeight, dialog);
        }
        finally
        {
            dialog.Close();
        }
    }

    // ────── T10: the German draft (every surface on under a test manifest, GermanDraft) ──────

    /// <summary>T10, binding: every German tray phrase, behind the German prefix, fits the tooltip cap.</summary>
    [Fact]
    public void Tray63_EveryGermanKey_FitsTheTooltip()
    {
        var german = GermanDraft.Table;
        var prefix = SupervisorStateText.TrayPrefix(german);
        var phrases = GermanDraft.ItemsOf("tray63").Where(i => i.Key != "supervisor.tray.prefix").ToList();
        Check.That(phrases).Not.IsEmpty();
        Check.WithCustomMessage("The German table resolved an English state phrase: the test manifest is not effective.")
            .That(SupervisorStateText.Describe(german, SupervisorState.Supervising)).IsEqualTo(GermanDraft.TextOf("supervisor.state.watching", ""));

        var failures = phrases.Select(p => (p.Key, Verdict: Tray(prefix, p.Text)))
            .Append(("supervisor.tray.prefix", Tray(prefix, phrases.MaxBy(p => p.Text.Length)!.Text)))
            .Where(r => { _output.WriteLine($"{r.Item1}: {r.Item2}"); return !r.Item2.Fits; })
            .Select(r => $"{r.Item1}: {r.Item2}")
            .ToList();

        Check.WithCustomMessage("German tray tooltips over the shell cap:\n  " + string.Join("\n  ", failures)).That(failures).IsEmpty();
    }

    /// <summary>
    /// T10, binding: the FallenBack dialog built in German fits as a whole — every button keeps its
    /// 110 DIP, the grown window fits the work area, no run breaks — and so does each German title,
    /// body and button label of classes C-fallback and B260x3 on its own.
    /// </summary>
    [Fact]
    public void FallenBack_InGerman_TheDialogAndEveryTextFit()
    {
        RunSta(() =>
        {
            var german = GermanDraft.Table;
            Check.WithCustomMessage("The FallenBack title resolved its English: the German table is not effective.")
                .That(german.T("supervisor.fallback.title")).IsNotEqualTo(StringTable.EmbeddedEnglish.T("supervisor.fallback.title"));
            var failures = new List<string>();
            var whole = FallenBack(null, null, null, german);
            _output.WriteLine($"the German dialog: {whole}");
            if (!whole.Fits) failures.Add($"the German dialog: {whole}");

            // T11: measured at 2.0, the German body is 273.78 DIP of content in the 320 DIP floor,
            // so German does NOT grow this class-C window — the button row keeps its full height
            // (the verdict's 110/110 above). If a later German grows past the floor, this fails and
            // forces a re-measure rather than letting the grown state go unexamined.
            Check.WithCustomMessage("German grew the FallenBack dialog past its 320 DIP floor: " + whole)
                .That(whole.Detail).Contains($"window {FitText.Dip(320)} ");

            foreach (var item in GermanDraft.ItemsOf("B260x3"))
            {
                var verdict = FallenBack(null, null, item.Text, german);
                _output.WriteLine($"{item} \"{item.Text}\": {verdict}");
                if (!verdict.Fits) failures.Add($"{item}: {verdict}");
            }

            foreach (var item in GermanDraft.ItemsOf("C-fallback"))
            {
                var isTitle = item.Key == "supervisor.fallback.title";
                var verdict = FallenBack(isTitle ? item.Text : null, isTitle ? null : item.Text, null, german);
                _output.WriteLine($"{item} \"{item.Text}\": {verdict}");
                if (!verdict.Fits) failures.Add($"{item}: {verdict}");
            }

            Check.WithCustomMessage("German FallenBack texts do not fit:\n  " + string.Join("\n  ", failures)).That(failures).IsEmpty();
        });
    }

    /// <summary>
    /// T10, binding: the setup window built in German reaches every row (REACH) and shows every text
    /// whole (CLIP) — the gate T09 bound under the pseudo-locale, on the real draft.
    /// </summary>
    [Fact]
    public void Setup_InGerman_ClipsNothing_AndReachesEveryRow()
    {
        RunSta(() =>
        {
            var german = GermanDraft.Table;
            Check.That(german.T("supervisor.setup.window-title")).IsNotEqualTo(StringTable.EmbeddedEnglish.T("supervisor.setup.window-title"));
            var result = SetupIn(german);
            _output.WriteLine(result.Verdict.ToString());
            Check.WithCustomMessage("In German the setup window loses a row:\n  " + string.Join("\n  ", result.Reach))
                .That(result.Reach).IsEmpty();
            Check.WithCustomMessage("In German a setup-window text does not show whole:\n  " + string.Join("\n  ", result.Clip))
                .That(result.Clip).IsEmpty();
        });
    }

    /// <summary>
    /// T09 PR 3 (review-04 D1b, generalised after CI run 38084111300): Supervisor setup is a RECORDED
    /// geometry exception — a scrolling form re-flows. Its texts wrap, so a longer translation moves
    /// every dwell control below a wrapping text DOWN. Which texts wrap depends on the machine as well
    /// as the language: the stress subtitle wraps a third line at a text column under 514 DIP, and
    /// CI's wider window frame gives 512 (this machine: 515), so on CI the whole form moved 17.29 DIP.
    /// Real German can do the same on the user's machine. It is accepted because the window lays its
    /// texts out once per language — <c>OnLanguageChanged</c> refreshes only the language row's note
    /// and the status line — like a Settings page re-paginating, never under a live dwell.
    ///
    /// <para>
    /// The rule, under the pseudo-locale and the stress table, at this machine's frame AND a wider
    /// one (so the whole-form shift is exercised on every machine): every dwell control keeps its x
    /// and its size; none moves up; a control lower in the form moves down by at least as much as any
    /// control above it, and controls on one row move together — so the order is preserved, top to
    /// bottom and in the tree. That the switches share one x and stay put inside their cards is
    /// pinned by <see cref="Setup_BothToggleSwitches_ShareOneX"/> and
    /// <see cref="Setup_TheToggleSwitches_StayPutInTheirCards_UnderThePseudoLocale"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void Setup_TheRecordedException_ControlsOnlyReflowDown_InOrder_NeverSideways()
    {
        RunSta(() =>
        {
            foreach (var extraFrame in new[] { 0.0, 1.5 })
            {
                var english = DwellControlBoxes(StringTable.EmbeddedEnglish, extraFrame);
                Check.WithCustomMessage("The form's dwell controls were not all found.")
                    .That(english.Select(c => c.Role).Count(r => r is "launch" or "save" or "switch")).IsEqualTo(4);
                Check.That(english.Count).IsEqualTo(7);

                foreach (var (name, table) in new[] { ("pseudo", PseudoLocale.Table), ("stress", PseudoLocale.StressTable), ("german", GermanDraft.Table) })
                {
                    var other = DwellControlBoxes(table, extraFrame);
                    var context = $"{name}, frame +{FitText.Dip(extraFrame)} DIP per side";
                    for (var i = 0; i < english.Count; i++)
                        _output.WriteLine($"{context} {english[i].Role}: {english[i].Box} → {other[i].Box}");
                    var problems = ReflowProblems(english, other);
                    Check.WithCustomMessage($"Supervisor setup ({context}): a translation moved a dwell control beyond the "
                                            + "recorded re-flow exception:\n  " + string.Join("\n  ", problems))
                        .That(problems).IsEmpty();
                }
            }
        });
    }

    /// <summary>
    /// Everything that breaks the re-flow rule between an English layout and a translated one: a
    /// changed tree order, a sideways move, a resize, a move up, a control that moved less than one
    /// above it, or a row that came apart.
    /// </summary>
    private static List<string> ReflowProblems(List<(string Role, Rect Box)> english, List<(string Role, Rect Box)> other)
    {
        const double tolerance = 0.01;
        var problems = new List<string>();
        if (!english.Select(c => c.Role).SequenceEqual(other.Select(c => c.Role)))
        {
            problems.Add($"the controls' order changed: {string.Join(", ", english.Select(c => c.Role))} → {string.Join(", ", other.Select(c => c.Role))}");
            return problems;
        }

        var moves = english.Select((c, i) => (c.Role, Before: c.Box, After: other[i].Box, Drop: other[i].Box.Y - c.Box.Y)).ToList();
        foreach (var m in moves)
        {
            if (Math.Abs(m.After.X - m.Before.X) > tolerance)
                problems.Add($"{m.Role}: moved sideways, x {FitText.Dip(m.Before.X)} → {FitText.Dip(m.After.X)}");
            if (Math.Abs(m.After.Width - m.Before.Width) > tolerance || Math.Abs(m.After.Height - m.Before.Height) > tolerance)
                problems.Add($"{m.Role}: resized, {m.Before.Size} → {m.After.Size}");
            if (m.Drop < -tolerance)
                problems.Add($"{m.Role}: moved UP by {FitText.Dip(-m.Drop)}");
        }

        foreach (var upper in moves)
        {
            foreach (var lower in moves.Where(l => l.Before.Y > upper.Before.Y + tolerance))
            {
                if (lower.Drop < upper.Drop - tolerance)
                    problems.Add($"{lower.Role} (y {FitText.Dip(lower.Before.Y)}) dropped {FitText.Dip(lower.Drop)}, less than "
                                 + $"{upper.Role} above it (y {FitText.Dip(upper.Before.Y)}) dropped {FitText.Dip(upper.Drop)}");
            }

            foreach (var peer in moves.Where(p => p.Role != upper.Role && Math.Abs(p.Before.Y - upper.Before.Y) <= tolerance))
            {
                if (Math.Abs(peer.Drop - upper.Drop) > tolerance && string.CompareOrdinal(upper.Role, peer.Role) < 0)
                    problems.Add($"{upper.Role} and {peer.Role} share a row but dropped {FitText.Dip(upper.Drop)} and {FitText.Dip(peer.Drop)}");
            }
        }

        return problems;
    }

    /// <summary>
    /// Every dwell control of the setup form (the hand-cursor roots), in tree order, with its role —
    /// "launch", "save", "switch", or "language row #i" — and its box in DIP relative to the form,
    /// laid out at its full height at 2.0 with the window's default (collapsed) Advanced section, in
    /// the client width this machine's frame leaves, narrowed by <paramref name="extraFrame"/> DIP a
    /// side (a wider frame elsewhere, such as CI's).
    /// </summary>
    private List<(string Role, Rect Box)> DwellControlBoxes(StringTable strings, double extraFrame)
    {
        var window = BuildSetupWindow(strings);
        try
        {
            var chrome = Chrome();
            var scroller = (ScrollViewer)window.Content;
            window.Content = null;
            var form = new Border { Background = window.Background, Child = scroller };
            using var host = FitHost.Open(form, width: window.Width - chrome.Left - chrome.Right - 2 * extraFrame, inheritFrom: window);

            var switches = new[] { "setup_enable_supervision", "setup_start_on_logon" }
                .Select(id => FitText.Descendants<TextBlock>(scroller)
                    .Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == $"Settings_Toggle_{id}"))
                .Select(word => FitText.Ancestors(word).OfType<Border>().First(b => b.Width == 160))
                .ToHashSet();
            var launch = strings.T("supervisor.launch-now");
            var save = strings.T("supervisor.setup.save");

            var result = new List<(string, Rect)>();
            var other = 0;
            foreach (var b in FitText.Descendants<Border>(scroller).Where(b => b.Cursor == System.Windows.Input.Cursors.Hand))
            {
                var caption = FitText.Descendants<TextBlock>(b).Select(t => t.Text).FirstOrDefault(t => t.Length > 0) ?? "";
                var role = switches.Contains(b) ? "switch" : caption == launch ? "launch" : caption == save ? "save" : $"language row #{other++}";
                result.Add((role, b.TransformToAncestor(form).TransformBounds(new Rect(b.RenderSize))));
            }

            return result;
        }
        finally
        {
            window.Close();
        }
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

    /// <summary>
    /// T09 PR 3, geometry invariance for the setup window's two toggles: each switch keeps its
    /// 160 × 100 box AND its place inside its own toggle card, in English and under the
    /// pseudo-locale, in physical px at 2.0. The cards themselves sit in a scrolling form, so a
    /// longer subtitle above them moves the whole card down — a flowing form re-lays out, and the
    /// REACH gate above guarantees every card still scrolls into view — but nothing inside a card
    /// may follow the text: its label wraps within the card's fixed 100 DIP height (with an
    /// ellipsis past it, which CLIP forbids) and its switch is a fixed-width right-hand column.
    /// </summary>
    [Fact]
    public void Setup_TheToggleSwitches_StayPutInTheirCards_UnderThePseudoLocale()
    {
        RunSta(() =>
        {
            var english = SwitchBoxes(StringTable.EmbeddedEnglish);
            var pseudo = SwitchBoxes(PseudoLocale.Table);
            var german = SwitchBoxes(GermanDraft.Table);
            foreach (var (id, box) in english)
                _output.WriteLine($"{id}: {box} (pseudo {pseudo[id]}, German {german[id]})");

            Check.That(english.Keys).ContainsExactly(pseudo.Keys);
            Check.That(english.Keys).ContainsExactly(german.Keys);
            Check.That(english).HasSize(2);
            foreach (var (id, box) in english)
            {
                Check.WithCustomMessage($"{id}: the switch moved inside its card, {box} → {pseudo[id]}")
                    .That(pseudo[id]).IsEqualTo(box);
                Check.WithCustomMessage($"{id}: the switch moved inside its card in German, {box} → {german[id]}")
                    .That(german[id]).IsEqualTo(box);
                Check.WithCustomMessage($"{id}: the switch is not 160 × 100 DIP at 2.0: {box}")
                    .That((box.Width, box.Height)).IsEqualTo((320.0, 200.0));
            }
        });
    }

    /// <summary>
    /// T09 PR 3 (option A): the two switches share one x — the right edge of the form column — in
    /// English and under the pseudo-locale. Before A each card was as wide as its label, so the two
    /// English switches sat 28 DIP apart.
    /// </summary>
    [Fact]
    public void Setup_BothToggleSwitches_ShareOneX()
    {
        RunSta(() =>
        {
            foreach (var strings in new[] { StringTable.EmbeddedEnglish, PseudoLocale.Table, GermanDraft.Table })
            {
                var layout = FormLayout(strings, leftAlignedCards: false);
                var xs = layout.Switches.Values.Select(r => r.X).ToList();
                _output.WriteLine($"{strings.T("supervisor.setup.toggle.on")}: switch x {string.Join(", ", xs.Select(FitText.Dip))}");
                Check.That(xs).HasSize(2);
                Check.WithCustomMessage("The two toggle switches are not aligned.").That(Math.Abs(xs[0] - xs[1])).IsLessOrEqualThan(1e-6);
            }
        });
    }

    /// <summary>
    /// T09 PR 3 (option A, the coordinator's check 4): stretching the toggle cards moves ONLY the two
    /// switches. Every other dwell control in the window — the T05b language row's three buttons,
    /// "Launch EyeCtrl now", "Save" — keeps its box, compared against today's left-aligned cards.
    /// </summary>
    [Fact]
    public void Setup_StretchingTheToggleCards_MovesOnlyTheirSwitches()
    {
        RunSta(() =>
        {
            var before = FormLayout(StringTable.EmbeddedEnglish, leftAlignedCards: true);
            var after = FormLayout(StringTable.EmbeddedEnglish, leftAlignedCards: false);
            Check.That(after.Controls.Keys).ContainsExactly(before.Controls.Keys);
            var moved = before.Controls.Keys.Where(k => before.Controls[k] != after.Controls[k]).ToList();
            foreach (var key in before.Controls.Keys)
                _output.WriteLine($"{key}: {before.Controls[key]} → {after.Controls[key]}");
            Check.WithCustomMessage("Stretching the toggle cards moved something other than their switches: "
                    + string.Join(", ", moved.Where(k => !before.Switches.ContainsKey(k))))
                .That(moved.Where(k => !before.Switches.ContainsKey(k))).IsEmpty();
            Check.That(before.Controls.Count).IsStrictlyGreaterThan(5);
        });
    }

    /// <summary>
    /// The before/after sheet for the user's decision on option A (not a gate): set
    /// <c>EYECTRL_WRITE_T09_TOGGLE_SHEET=1</c> and it writes
    /// <c>C:\t\keys\t09-supervisor-toggle-alignment.png</c> — the whole setup form at 2.0 in English,
    /// today's left-aligned cards on the left and A's stretched cards on the right, each switch's
    /// centre marked and labelled.
    /// </summary>
    [Fact]
    public void ToggleAlignmentSheet_WhenAsked()
    {
        if (Environment.GetEnvironmentVariable("EYECTRL_WRITE_T09_TOGGLE_SHEET") != "1") return;
        RunSta(() =>
        {
            var before = FormLayout(StringTable.EmbeddedEnglish, leftAlignedCards: true, render: true);
            var after = FormLayout(StringTable.EmbeddedEnglish, leftAlignedCards: false, render: true);
            const double gap = 40, header = 70;
            var width = before.Size.Width + gap + after.Size.Width;
            var height = header + Math.Max(before.Size.Height, after.Size.Height);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)), null, new Rect(0, 0, width, height));
                void Panel(FormLayoutResult layout, double x, string title)
                {
                    dc.DrawText(Text(title, 22, Brushes.White), new Point(x + 10, 18));
                    dc.DrawImage(layout.Image!, new Rect(x, header, layout.Size.Width, layout.Size.Height));
                    foreach (var (name, r) in layout.Switches)
                    {
                        var c = new Point(x + r.X + r.Width / 2, header + r.Y + r.Height / 2);
                        var pen = new Pen(Brushes.Red, 3);
                        dc.DrawEllipse(null, pen, c, 50, 50);
                        dc.DrawLine(pen, new Point(c.X - 14, c.Y), new Point(c.X + 14, c.Y));
                        dc.DrawLine(pen, new Point(c.X, c.Y - 14), new Point(c.X, c.Y + 14));
                        var label = Text($"switch centre x = {FitText.Dip(c.X - x)} DIP", 15, Brushes.Red);
                        var at = new Point(c.X - label.Width / 2, c.Y - 50 - label.Height - 6);
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x10, 0x10)), null,
                            new Rect(at.X - 4, at.Y - 2, label.Width + 8, label.Height + 4));
                        dc.DrawText(label, at);
                    }
                }

                Panel(before, 0, "Today: cards sized to their labels");
                Panel(after, before.Size.Width + gap, "Option A: cards stretched, switches aligned");
            }

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * 2), (int)Math.Ceiling(height * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(@"C:\t\keys");
            using var file = File.Create(@"C:\t\keys\t09-supervisor-toggle-alignment.png");
            encoder.Save(file);
        });

        static FormattedText Text(string s, double size, Brush brush) => new(s, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 2.0);
    }

    private sealed record FormLayoutResult(
        Dictionary<string, Rect> Controls, Dictionary<string, Rect> Switches, Size Size, ImageSource? Image);

    /// <summary>
    /// The whole setup form at 2.0, its full height laid out (no scrolling), with the toggle cards
    /// stretched (A) or left-aligned (today). Every dwell control's box, keyed by order and caption,
    /// in DIP relative to the form; the two switches' boxes; and, when asked, a render.
    /// </summary>
    private FormLayoutResult FormLayout(StringTable strings, bool leftAlignedCards, bool render = false)
    {
        var window = BuildSetupWindow(strings);
        try
        {
            window.AdvancedExpander.IsExpanded = true;
            if (leftAlignedCards)
            {
                foreach (var card in window.ToggleHost.Children.OfType<Border>().Where(b => b.MinWidth == 360))
                    card.HorizontalAlignment = HorizontalAlignment.Left;
            }

            var chrome = Chrome();
            var scroller = (ScrollViewer)window.Content;
            window.Content = null;
            var form = new Border { Background = window.Background, Child = scroller };
            using var host = FitHost.Open(form, width: window.Width - chrome.Left - chrome.Right, inheritFrom: window);

            // Relative to the whole form (the rendered image), not the inner panel and its 20 DIP margin.
            var root = (Visual)form;
            var controls = new Dictionary<string, Rect>(StringComparer.Ordinal);
            var index = 0;
            foreach (var b in FitText.Descendants<Border>(scroller).Where(b => b.Cursor == System.Windows.Input.Cursors.Hand))
            {
                var caption = FitText.Descendants<TextBlock>(b).Select(t => t.Text).FirstOrDefault(t => t.Length > 0) ?? "";
                var key = $"#{index++} '{caption}'";
                controls[key] = b.TransformToAncestor(root).TransformBounds(new Rect(b.RenderSize));
            }

            var switches = new Dictionary<string, Rect>(StringComparer.Ordinal);
            foreach (var id in new[] { "setup_enable_supervision", "setup_start_on_logon" })
            {
                var word = FitText.Descendants<TextBlock>(scroller)
                    .Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == $"Settings_Toggle_{id}");
                var sw = FitText.Ancestors(word).OfType<Border>().First(b => b.Width == 160);
                var key = controls.Single(pair => pair.Value == sw.TransformToAncestor(root).TransformBounds(new Rect(sw.RenderSize))).Key;
                switches[key] = controls[key];
            }

            ImageSource? image = null;
            var size = new Size(form.ActualWidth, form.ActualHeight);
            if (render)
            {
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(form), null, new Rect(size));
                rtb.Render(dv);
                rtb.Freeze();
                image = rtb;
            }

            return new FormLayoutResult(controls, switches, size, image);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Each toggle's switch box relative to its toggle card, in physical px at 2.0.</summary>
    private Dictionary<string, Rect> SwitchBoxes(StringTable strings)
    {
        var window = BuildSetupWindow(strings);
        try
        {
            var chrome = Chrome();
            var scroller = (ScrollViewer)window.Content;
            window.Content = null;
            using var host = FitHost.Open(scroller, width: window.Width - chrome.Left - chrome.Right,
                height: window.Height - chrome.Top - chrome.Bottom, inheritFrom: window);

            var boxes = new Dictionary<string, Rect>(StringComparer.Ordinal);
            foreach (var id in new[] { "setup_enable_supervision", "setup_start_on_logon" })
            {
                var word = FitText.Descendants<TextBlock>(scroller)
                    .Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == $"Settings_Toggle_{id}");
                var switchBorder = FitText.Ancestors(word).OfType<Border>().First(b => b.Width == 160);
                var card = (FrameworkElement)FitText.Ancestors(switchBorder)
                    .First(a => a is FrameworkElement fe && ReferenceEquals(fe.Parent, window.ToggleHost));
                var topLeft = switchBorder.TranslatePoint(new Point(0, 0), card);
                var size = new Size(switchBorder.ActualWidth * FitHost.Scale, switchBorder.ActualHeight * FitHost.Scale);
                boxes[id] = new Rect(new Point(topLeft.X * FitHost.Scale, topLeft.Y * FitHost.Scale), size);
            }

            Check.That(PseudoLocale.Table.T("supervisor.setup.toggle.on")).StartsWith("[");
            return boxes;
        }
        finally
        {
            window.Close();
        }
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
