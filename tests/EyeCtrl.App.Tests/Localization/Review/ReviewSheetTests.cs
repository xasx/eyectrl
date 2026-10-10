namespace EyeCtrl.App.Tests.Localization.Review;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization.Fit;
using EyeCtrl.App.Tests.Settings;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T10's review sheets for the user's one sitting (the format the user agreed on 2026-10-10):
/// six PNG sheets, one per area, each with ONE question — "All good" or "Change some", naming the
/// numbers. Every item shows the shipped English next to the German draft, rendered headless at
/// 2.0 through production's own windows and controls, so a sheet is 3840 × 2400 px — the user's
/// screen at 1:1.
///
/// <para>
/// Not a gate: it writes only when <c>EYECTRL_WRITE_T10_REVIEW=1</c> (see <see cref="ReviewRender"/>),
/// and never shows a window. The Supervisor's windows come from EyeCtrl.Supervisor.Tests'
/// <c>ReviewFragments_WhenAsked</c>, which must run first. The German is read from the repo's
/// <c>strings.de.json</c> (<see cref="GermanDraft"/>), so re-running after a wording change
/// re-renders every sheet.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class ReviewSheetTests
{
    private const double SheetWidth = 1920;
    private const double SheetHeight = 1200;

    /// <summary>A Settings column narrower than the user's 1424 but still two-column (at least 808), so a row fits a sheet column.</summary>
    private const double RowWidth = 900;

    private static readonly Brush SheetBackground = Frozen(Color.FromRgb(0x12, 0x12, 0x12));
    private static readonly Brush CardBackground = Frozen(Color.FromRgb(0x1D, 0x1D, 0x1F));
    private static readonly Brush CardBorder = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3E));
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xEE, 0xEE, 0xEE));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0xA0, 0xA0, 0xA6));
    private static readonly Brush GermanInk = Frozen(Color.FromRgb(0x8F, 0xE3, 0x9B));
    private static readonly Brush FlagInk = Frozen(Color.FromRgb(0xF5, 0xC5, 0x42));
    private static readonly Brush Badge = Frozen(Color.FromRgb(0x2D, 0x5A, 0x89));
    private static readonly Brush Desktop = Frozen(Color.FromRgb(0x3B, 0x4A, 0x5A));
    private static readonly FontFamily SheetFont = new("Segoe UI");

    private readonly ITestOutputHelper _output;
    private int _number;

    public ReviewSheetTests(ITestOutputHelper output) => _output = output;

    [StaFact]
    public void ReviewSheets_WhenAsked()
    {
        if (!ReviewRender.Requested) return;
        WpfApplicationGate.EnsureApplicationWithTokens();
        var english = StringTable.EmbeddedEnglish;
        var german = GermanDraft.Table;

        foreach (var path in new[]
                 {
                     GlossarySheet(),
                     CrisisSheet(english, german),
                     SupervisorSheet(english, german),
                     RecoverySheet(english, german),
                     DestructiveSheet(english, german),
                     RingSheet(english, german),
                 })
        {
            _output.WriteLine(path);
        }
    }

    // ────── Sheet 1: the glossary ──────

    private string GlossarySheet()
    {
        _number = 0;
        var sheet = new Sheet(1, "The glossary — one German word per idea, used on every screen",
            "Are these the right German words? Answer \"All good\", or \"Change some\" and say the numbers.");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(GermanDraft.RepoRoot(), "localization", "glossary.de.json")));
        var entries = document.RootElement.GetProperty("entries").EnumerateArray().ToList();
        var flags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["supervisor"] = "A CHOICE: we keep the program's name \"Supervisor\". Or a German name such as »Wächter«?",
            ["double-click"] = "On the ring \"2× Klick\": \"Doppelklick\" does not fit there (see sheet 6).",
            ["caregiver"] = "Alert titles name no one: \"Bitte kurz nachsehen\".",
        };

        var rows = new List<(int Number, string English, string Short, string Long, string? Flag)>();
        foreach (var entry in entries)
        {
            var id = entry.GetProperty("id").GetString()!;
            var shortForm = entry.GetProperty("de-short").GetString()!;
            var longForm = entry.GetProperty("de-long").GetString()!;
            rows.Add((++_number, entry.GetProperty("en").GetString()!, shortForm, longForm == shortForm ? "" : longForm,
                flags.GetValueOrDefault(id)));
        }

        var half = (rows.Count + 1) / 2;
        sheet.Add(GlossaryTable(rows.Take(half).ToList()));
        sheet.NextColumn();
        sheet.Add(GlossaryTable(rows.Skip(half).ToList()));
        return sheet.Write("1-glossary.png", _output);
    }

    private static FrameworkElement GlossaryTable(IReadOnlyList<(int Number, string English, string Short, string Long, string? Flag)> rows)
    {
        var grid = new Grid { Width = 930 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Cell(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        grid.RowDefinitions.Add(new RowDefinition());
        Cell(Text("English", 15, Dim), 0, 1);
        Cell(Text("Deutsch (short · long)", 15, GermanInk), 0, 2);
        for (var i = 0; i < rows.Count; i++)
        {
            var (number, english, shortForm, longForm, flag) = rows[i];
            grid.RowDefinitions.Add(new RowDefinition());
            var r = i + 1;
            Cell(Text(number.ToString(CultureInfo.InvariantCulture), 16, Dim, margin: new Thickness(0, 5, 0, 5)), r, 0);
            Cell(Text(english, 16, Dim, wrap: true, margin: new Thickness(0, 5, 12, 5)), r, 1);
            var german = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
            german.Children.Add(Text(longForm.Length == 0 ? shortForm : $"{shortForm}  ·  {longForm}", 18, GermanInk, bold: true, wrap: true));
            if (flag is not null) german.Children.Add(Text("⚑ " + flag, 15, FlagInk, wrap: true));
            Cell(german, r, 2);
        }

        return grid;
    }

    // ────── Sheet 2: crisis screens ──────

    private string CrisisSheet(StringTable english, StringTable german)
    {
        _number = 0;
        var sheet = new Sheet(2, "Crisis screens — read by the user, or by a caregiver in a hurry",
            "Is the German right on all of these? Answer \"All good\", or \"Change some\" and say the numbers.");
        sheet.Add(Pair("EyeCtrl stopped and a backup program took over (Supervisor dialog)",
            ReviewRender.Load(ReviewRender.FragmentPath("fallenback-en")), ReviewRender.Load(ReviewRender.FragmentPath("fallenback-de")), scale: 0.7));
        sheet.Add(Pair("Caregiver alert: tracking unreliable", Alert(english, "caregiver-alert.poor-tracking"), Alert(german, "caregiver-alert.poor-tracking"), stacked: true, scale: 0.85));
        sheet.Add(Pair("Caregiver alert: the user is struggling", Alert(english, "caregiver-alert.struggling"), Alert(german, "caregiver-alert.struggling"), stacked: true, scale: 0.85));
        sheet.NextColumn();
        sheet.Add(Pair("Drift notice", Drift(english), Drift(german), stacked: true));
        sheet.Add(Pair("Startup error (a Windows message box, drawn here)", StartupError(english), StartupError(german), stacked: true, scale: 0.8));
        sheet.Add(Pair("Eye-care exercise: the button that ends it early (shown twice as large)", Skip(english), Skip(german),
            flag: "\"Ende ✕\" says END, where the English says Skip: \"Überspringen ✕\" does not fit the button. The button ends the exercise."));
        return sheet.Write("2-crisis.png", _output);
    }

    private static BitmapSource Alert(StringTable strings, string key) => In(strings, () =>
    {
        var window = new CaregiverAlertWindow();
        window.Prepare(strings.T(key), AppFitMeasurers.ReferenceWorkArea);
        window.Banner.Opacity = 1;   // it fades in on show
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        return ReviewRender.Snap(root, window.Width, window.Height, window, background: Desktop);
    });

    private static BitmapSource Drift(StringTable strings) => In(strings, () =>
    {
        var window = new DriftNoticeWindow();
        window.Banner.Opacity = 1;   // it fades in on show
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        return ReviewRender.Snap(root, window.Width, window.Height, window, background: Desktop);
    });

    private static BitmapSource StartupError(StringTable strings) => In(strings, () =>
    {
        var text = StartupErrorText.Resolve("The process cannot access the file 'settings.json' because it is being used by another process.");
        var box = new Border
        {
            Width = 560,
            Background = Brushes.White,
            BorderBrush = Frozen(Color.FromRgb(0xC8, 0xC8, 0xC8)),
            BorderThickness = new Thickness(1),
        };
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Background = Frozen(Color.FromRgb(0xF3, 0xF3, 0xF3)),
            Padding = new Thickness(12, 8, 12, 8),
            Child = Text(text.Title, 14, Brushes.Black, family: new FontFamily("Segoe UI")),
        });
        stack.Children.Add(Text(text.Message, 15, Brushes.Black, wrap: true, margin: new Thickness(24, 20, 24, 20), family: new FontFamily("Segoe UI")));
        stack.Children.Add(new Border
        {
            Background = Frozen(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            Padding = new Thickness(12),
            Child = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Width = 88,
                BorderBrush = Frozen(Color.FromRgb(0x00, 0x67, 0xC0)),
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Padding = new Thickness(4),
                Child = Text("OK", 14, Brushes.Black, center: true, family: new FontFamily("Segoe UI")),
            },
        });
        box.Child = stack;
        return ReviewRender.Snap(box);
    });

    private static BitmapSource Skip(StringTable strings) => In(strings, () =>
    {
        var window = new EyeCareExerciseWindow();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, 1920, 1080, window);
        var button = window.SkipButton;
        var box = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
        box.Inflate(40, 24);
        return RenderRegion(root, box, Brushes.Black, zoom: 2);
    });

    // ────── Sheet 3: the Supervisor ──────

    private string SupervisorSheet(StringTable english, StringTable german)
    {
        _number = 0;
        var sheet = new Sheet(3, "The Supervisor — the watchdog program a caregiver sets up",
            "Is the German right on all of these? Answer \"All good\", or \"Change some\" and say the numbers.");
        sheet.Add(Pair("Supervisor setup window",
            ReviewRender.Load(ReviewRender.FragmentPath("setup-en")), ReviewRender.Load(ReviewRender.FragmentPath("setup-de")), scale: 0.8));
        sheet.NextColumn();
        var states = new[] { "watching", "starting", "restarting", "fallen-back", "closing", "idle" };
        sheet.Add(Pair("Tray tooltip, one per state",
            Tooltips(states.Select(s => english.T("supervisor.tray.prefix") + english.T("supervisor.state." + s))),
            Tooltips(states.Select(s => german.T("supervisor.tray.prefix") + german.T("supervisor.state." + s)))));
        sheet.Add(Pair("Tray menu",
            Menu([english.T("supervisor.tray.settings"), english.T("supervisor.tray.exit")]),
            Menu([german.T("supervisor.tray.settings"), german.T("supervisor.tray.exit")])));
        sheet.Add(TextPairs("Setup window messages after Save and on the logon switch",
            ["supervisor.setup.saved", "supervisor.setup.save-failed", "supervisor.setup.launch-requested", "supervisor.setup.supervision-enabled",
             "supervisor.setup.logon.saved-on", "supervisor.setup.logon.saved-off", "supervisor.setup.logon.parked", "supervisor.setup.logon.waiting",
             "supervisor.setup.logon.refused", "supervisor.setup.logon.superseded"],
            english, german, args: ["(the reason, in English)"], width: 760));
        return sheet.Write("3-supervisor.png", _output);
    }

    // ────── Sheet 4: the way back from a wrong language ──────

    private string RecoverySheet(StringTable english, StringTable german)
    {
        _number = 0;
        var sheet = new Sheet(4, "The way back from a wrong language — Settings, the restart, the Supervisor",
            "Is the German right on all of these? Answer \"All good\", or \"Change some\" and say the numbers.");
        sheet.Add(Pair("Settings, General, first row: the language", LanguageRow(english), LanguageRow(german), stacked: true, scale: 0.75));
        sheet.Add(Pair("After a new language is picked: the restart prompt",
            Modal(english, "language-recovery.prompt.message", "language-recovery.prompt.restart", "language-recovery.prompt.cancel"),
            Modal(german, "language-recovery.prompt.message", "language-recovery.prompt.restart", "language-recovery.prompt.cancel"), scale: 0.75));
        sheet.Add(Pair("Tray menu of EyeCtrl (the language item stays bilingual)",
            Menu(TrayItems(english)), Menu(TrayItems(german))));
        sheet.NextColumn();
        sheet.Add(Pair("While EyeCtrl restarts",
            Modal(english, "language-recovery.restart.in_progress.message", "language-recovery.restart.in_progress.hide"),
            Modal(german, "language-recovery.restart.in_progress.message", "language-recovery.restart.in_progress.hide"), stacked: true, scale: 0.7));
        sheet.Add(TextPairs("While a new language waits for the restart (bilingual on purpose)",
            ["language-recovery.note.pending"], english, german, width: 460));
        sheet.NextColumn();
        sheet.Add(TextPairs("If the restart fails (shown with \"✓ OK\")",
            ["language-recovery.restart.failed.generic", "language-recovery.restart.failed.child_not_ready", "language-recovery.restart.failed.child_exited_early",
             "language-recovery.restart.failed.launch_failed", "language-recovery.restart.failed.no_exe_path", "language-recovery.restart.failed.frame"],
            english, german, args: ["(technical detail, in English)."], width: 640));
        sheet.Add(TextPairs("Supervisor setup, under its language buttons",
            ["language-recovery.supervisor.sending", "language-recovery.supervisor.saved", "language-recovery.supervisor.saved-restarting",
             "language-recovery.supervisor.pending", "language-recovery.supervisor.waiting", "language-recovery.supervisor.refused",
             "language-recovery.supervisor.superseded"],
            english, german, width: 640));
        return sheet.Write("4-recovery.png", _output);
    }

    private static string[] TrayItems(StringTable strings) =>
    [
        strings.T("app-tray.settings"), strings.T("language-recovery.tray.item"), strings.T("app-tray.show-toolbar"),
        strings.T("app-tray.pause"), strings.T("app-tray.reset-adaptation"), strings.T("app-tray.exit"),
    ];

    private static BitmapSource LanguageRow(StringTable strings) => In(strings, () =>
        HandChunkedPageFitTests.WithPagesOf(typeof(GeneralPageSource), RowWidth, pages =>
        {
            var row = pages[0].Rows.First(r => r.Label == strings.T("language-recovery.row.label"));
            var panel = HandChunkedPageFitTests.BuildPagePanel([row], RowWidth);
            var card = new Border { Background = Tokens.BrushSurfaceMedium, Padding = new Thickness(16), Child = panel };
            return ReviewRender.Snap(card, family: Tokens.FontStandard);
        }));

    // ────── Sheet 5: destructive confirmations ──────

    private string DestructiveSheet(StringTable english, StringTable german)
    {
        _number = 0;
        var sheet = new Sheet(5, "Destructive buttons in Settings, and the question each one asks",
            "Is the German right on all of these? Answer \"All good\", or \"Change some\" and say the numbers.");
        sheet.Add(Pair("Reset ALL settings: the button, then its question",
            Stack(SettingsButton(english, "settings.general.resetall.label", 300, 100), Modal(english, "settings.general.resetall.message", "settings.modal.yes", "settings.modal.no")),
            Stack(SettingsButton(german, "settings.general.resetall.label", 300, 100), Modal(german, "settings.general.resetall.message", "settings.modal.yes", "settings.modal.no")), scale: 0.6));
        sheet.Add(Pair("Each tab's reset button (here: Cursor), and its question — the same pattern on all 11 tabs",
            Stack(SettingsButton(english, "settings.cursor.reset.label", 300, 100), Modal(english, "settings.cursor.reset.message", "settings.modal.yes", "settings.modal.no")),
            Stack(SettingsButton(german, "settings.cursor.reset.label", 300, 100), Modal(german, "settings.cursor.reset.message", "settings.modal.yes", "settings.modal.no")), scale: 0.6));
        sheet.Add(Pair("Remove all learned words (here: 37 of them)",
            Stack(SettingsButton(english, "settings.keyboard.dictionary.clear.label", 420, 100), Modal(english, "settings.keyboard.dictionary.clear.message", [37], "settings.modal.yes", "settings.modal.no")),
            Stack(SettingsButton(german, "settings.keyboard.dictionary.clear.label", 420, 100), Modal(german, "settings.keyboard.dictionary.clear.message", [37], "settings.modal.yes", "settings.modal.no")), scale: 0.6));
        sheet.NextColumn();
        sheet.Add(Grid4("The other tabs' reset buttons, in German",
            ["settings.action_menu.reset.label", "settings.adaptation.reset.label", "settings.advanced.reset.label", "settings.diagnostics.reset.label",
             "settings.dwell.reset.label", "settings.eyecare.reset.label", "settings.general.reset.label", "settings.keyboard.reset.label",
             "settings.layout.reset.label", "settings.toolbar.reset.label"], german));
        var texts = TextPairs("The other questions before something is deleted",
            ["settings.calibration.clear.message", "settings.adaptation.reset_learned.message", "settings.diagnostics.reset.message",
             "settings.models.delete.message", "settings.quick_actions.delete.message", "settings.keyboard.dictionary.remove.message",
             "settings.general.reset.message"],
            english, german, args: ["SmolLM2 135M"], width: 640,
            argsFor: new() { ["settings.quick_actions.delete.message"] = (["Copy"], ["Kopieren"]), ["settings.keyboard.dictionary.remove.message"] = (["hello"], ["Hallo"]) });
        sheet.Add(texts);
        return sheet.Write("5-destructive.png", _output);
    }

    private static BitmapSource SettingsButton(StringTable strings, string key, double width, double height) => In(strings, () =>
    {
        var spec = new ButtonSpec("review", strings.T(key), () => { }, ButtonKind.Reset, width, height);
        var button = (DwellButton)SettingsWindow.CreateControl(
            spec, new SettingsAutoPagination.NoopDwellRegistrar(), SettingsWindow.ControlBtnRadius, 800, 1424);
        var panel = new StackPanel();
        button.Attach(panel);
        var card = new Border { Background = Tokens.BrushSurfaceMedium, Padding = new Thickness(8), Child = panel };
        return ReviewRender.Snap(card, family: Tokens.FontStandard);
    });

    private static BitmapSource Modal(StringTable strings, string messageKey, params string[] choiceKeys) =>
        Modal(strings, messageKey, [], choiceKeys);

    private static BitmapSource Modal(StringTable strings, string messageKey, object?[] args, params string[] choiceKeys) =>
        In(strings, () => SettingsModalExceptionTests.RenderCard(strings.T(messageKey, args), choiceKeys.Select(k => strings.T(k)).ToList()));

    // ────── Sheet 6: the ring labels ──────

    private string RingSheet(StringTable english, StringTable german)
    {
        _number = 0;
        var sheet = new Sheet(6, "The action ring and the modifier keys",
            "Are the ring labels right? Answer \"All good\", or \"Change some\" and say the numbers.");
        var rings = new StackPanel { Orientation = Orientation.Horizontal };
        rings.Children.Add(Labelled("First ring (the default four actions, More, Back)", Pic(Ring(german, RingLayer.Primary), 0.75), GermanInk));
        rings.Children.Add(Labelled("Second ring (behind More)", Pic(Ring(german, RingLayer.Secondary), 0.75), GermanInk));
        sheet.Add(Card("The ring in German, with its labels on (they show after looking at Help); each label's English is in 3", rings));
        sheet.Add(Pair("The modifier keys beside the action menu", Modifiers(english), Modifiers(german)));
        sheet.NextColumn();
        sheet.Add(TextPairs("Every ring label: English, then German",
            [.. Enum.GetValues<InteractionAction>().Select(a => RingLabelMap.For(a)), RingLabelMap.For(null), "overlays.ring.more"],
            english, german, width: 600));
        var doubleClick = new StackPanel { Orientation = Orientation.Horizontal };
        doubleClick.Children.Add(Labelled("English (accepted as is)", Slot("overlays.ring.doubleClick", english.T("overlays.ring.doubleClick")), Dim));
        doubleClick.Children.Add(Labelled("Deutsch (chosen)", Slot("overlays.ring.doubleClick", german.T("overlays.ring.doubleClick")), GermanInk));
        doubleClick.Children.Add(Labelled("Doppelklick (passt nicht)", Slot("overlays.ring.doubleClick", "Doppelklick"), FlagInk));
        sheet.Add(Card("Double click on the ring", doubleClick,
            "A CHOICE: \"Doppelklick\" touches the ring's edge (the two p's hang low), so the ring says \"2× Klick\". Settings still says \"Doppelklick\"."));
        return sheet.Write("6-ring.png", _output);
    }

    private static BitmapSource Ring(StringTable strings, RingLayer layer) => In(strings, () =>
    {
        var labels = new RingLabelSource(
            action => strings.T(RingLabelMap.For(action)), strings.T(RingLabelMap.For(null)), strings.T("overlays.ring.more"));
        var plan = RingPlan.Create(Enum.GetValues<InteractionAction>(),
            [InteractionAction.LeftClick, InteractionAction.RightClick, InteractionAction.DoubleClick, InteractionAction.ScrollMode], 250, labels);
        var built = plan.BuildLayer(layer, showBack: true);
        var side = plan.WindowSize;
        var canvas = new Canvas { Width = side, Height = side, Background = Desktop };
        var centre = new Point(side / 2, side / 2);
        var slots = built.Slots.Select(slot =>
        {
            var radians = slot.AngleDegrees * Math.PI / 180;
            return new RingPaintSlot(slot.Id,
                new Point(centre.X + plan.EffectiveRingRadius * Math.Cos(radians), centre.Y + plan.EffectiveRingRadius * Math.Sin(radians)),
                slot.Icon, slot.Label, slot.Kind);
        }).ToList();
        ActionMenuRingPainter.Paint(canvas, slots, RingPaintOptions.Runtime(showLabels: true));
        using var host = FitHost.Open(canvas, family: Tokens.FontStandard);
        var reach = plan.EffectiveRingRadius + LayoutConstants.ActionMenuButtonRadius + 12;
        return RenderRegion(canvas, new Rect(centre.X - reach, centre.Y - reach, 2 * reach, 2 * reach), Desktop);
    });

    /// <summary>One ring slot with <paramref name="text"/> as its label, and the measured clearance written under it.</summary>
    private static FrameworkElement Slot(string key, string text)
    {
        var canvas = new Canvas { Width = 240, Height = 240, Background = Desktop };
        ActionMenuRingPainter.Paint(canvas,
            [new RingPaintSlot("review", new Point(120, 120), AppFitMeasurers.RingIconFor(key), text, ActionMenuItemKind.Action)],
            RingPaintOptions.Runtime(showLabels: true));
        var image = ReviewRender.Snap(canvas, family: Tokens.FontStandard);
        var verdict = AppFitMeasurers.Measurers["A105"](key, text);
        var panel = new StackPanel();
        panel.Children.Add(Pic(image));
        var clear = (verdict.Margin + 2).ToString("0.0", CultureInfo.InvariantCulture);
        panel.Children.Add(Text(verdict.Fits ? $"{clear} DIP clear of the edge" : $"only {clear} DIP clear (needs 2)",
            15, verdict.Fits ? GermanInk : FlagInk, margin: new Thickness(0, 6, 0, 0)));
        return panel;
    }

    private static BitmapSource Modifiers(StringTable strings) => In(strings, () =>
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, key) in new[]
                 {
                     (ActionMenuModifierButtons.ShiftId, "overlays.modifier.shift"),
                     (ActionMenuModifierButtons.CtrlId, "overlays.modifier.ctrl"),
                     (ActionMenuModifierButtons.AltId, "overlays.modifier.alt"),
                 })
        {
            var definition = ActionMenuModifierButtons.Definitions(600, new HashSet<ModifierKey>()).Single(d => d.Id == id) with { Label = strings.T(key) };
            var (button, _, window) = ArcLabelFit.BuildButtonInWindow(definition);
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            using var host = FitHost.Open(root, inheritFrom: window);
            var disc = button.Background.TransformToAncestor(root).TransformBounds(new Rect(button.Background.RenderSize));
            disc.Inflate(10, 10);
            var image = Pic(RenderRegion(root, disc, Desktop));
            image.Margin = new Thickness(0, 0, 10, 0);
            row.Children.Add(image);
        }

        return ReviewRender.Snap(row);
    });

    // ────── Sheet parts ──────

    private FrameworkElement Pair(string caption, BitmapSource english, BitmapSource german, bool stacked = false, string? flag = null,
        double scale = 1)
    {
        var images = new StackPanel { Orientation = stacked ? Orientation.Vertical : Orientation.Horizontal };
        images.Children.Add(Labelled("English", Pic(english, scale), Dim));
        images.Children.Add(Labelled("Deutsch", Pic(german, scale), GermanInk));
        return Card(caption, images, flag);
    }

    private FrameworkElement TextPairs(string caption, IReadOnlyList<string> keys, StringTable english, StringTable german,
        object?[]? args = null, Dictionary<string, (object?[] English, object?[] German)>? argsFor = null, double width = 900)
    {
        var grid = new Grid { Width = width };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            object?[] shared = Placeholders(english, key) > 0 ? args ?? [] : [];
            var (enArgs, deArgs) = argsFor is not null && argsFor.TryGetValue(key, out var own) ? own : (shared, shared);
            grid.RowDefinitions.Add(new RowDefinition());
            var en = Text(english.T(key, enArgs), 16, Dim, wrap: true, margin: new Thickness(0, 4, 14, 4));
            var de = Text(german.T(key, deArgs), 18, GermanInk, wrap: true, margin: new Thickness(0, 4, 0, 4));
            Grid.SetRow(en, i);
            Grid.SetRow(de, i);
            Grid.SetColumn(de, 1);
            grid.Children.Add(en);
            grid.Children.Add(de);
        }

        return Card(caption, grid);
    }

    private static int Placeholders(StringTable english, string key) =>
        System.Text.RegularExpressions.Regex.Matches(english.T(key), @"\{\d").Count;

    private FrameworkElement Grid4(string caption, IReadOnlyList<string> keys, StringTable strings)
    {
        var wrap = new WrapPanel { Width = 660 };
        foreach (var key in keys)
            wrap.Children.Add(Pic(SettingsButton(strings, key, 300, 100), scale: 0.66));
        return Card(caption, wrap);
    }

    private FrameworkElement Card(string caption, UIElement content, string? flag = null)
    {
        var number = ++_number;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = Badge,
            Child = Text(number.ToString(CultureInfo.InvariantCulture), 22, Brushes.White, bold: true, center: true),
        });
        head.Children.Add(Text(caption, 19, Ink, margin: new Thickness(12, 7, 0, 0)));
        var stack = new StackPanel();
        stack.Children.Add(head);
        if (flag is not null) stack.Children.Add(Text("⚑ " + flag, 17, FlagInk, wrap: true, margin: new Thickness(52, 0, 0, 8), maxWidth: 600));
        stack.Children.Add(content);
        return new Border
        {
            Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14), Margin = new Thickness(0, 0, 14, 14), Child = stack, HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private static FrameworkElement Labelled(string label, UIElement content, Brush brush)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 18, 8) };
        stack.Children.Add(Text(label, 15, brush, bold: true, margin: new Thickness(0, 0, 0, 4)));
        stack.Children.Add(content);
        return stack;
    }

    private static BitmapSource Stack(params BitmapSource[] parts)
    {
        var stack = new StackPanel();
        foreach (var part in parts)
        {
            var image = Pic(part);
            image.HorizontalAlignment = HorizontalAlignment.Left;
            image.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(image);
        }

        return ReviewRender.Snap(stack);
    }

    private static BitmapSource Tooltips(IEnumerable<string> lines)
    {
        var stack = new StackPanel();
        foreach (var line in lines)
        {
            stack.Children.Add(new Border
            {
                Background = Frozen(Color.FromRgb(0xF9, 0xF9, 0xF9)), BorderBrush = Frozen(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = Text(line, 14, Brushes.Black, family: new FontFamily("Segoe UI")),
            });
        }

        return ReviewRender.Snap(stack);
    }

    private static BitmapSource Menu(IReadOnlyList<string> items)
    {
        var stack = new StackPanel { Background = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2)), MinWidth = 220 };
        foreach (var item in items)
            stack.Children.Add(Text(item, 15, Brushes.Black, margin: new Thickness(32, 7, 24, 7), family: new FontFamily("Segoe UI")));
        return ReviewRender.Snap(new Border
        {
            BorderBrush = Frozen(Color.FromRgb(0xA0, 0xA0, 0xA0)), BorderThickness = new Thickness(1), Child = stack,
        });
    }

    private static Image Pic(BitmapSource source, double scale = 1)
    {
        var size = ReviewRender.DipSize(source);
        return new Image
        {
            Source = source, Width = size.Width * scale, Height = size.Height * scale, Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
    }

    private static TextBlock Text(string text, double size, Brush brush, bool bold = false, bool wrap = false, bool center = false,
        Thickness margin = default, FontFamily? family = null, double maxWidth = double.PositiveInfinity) => new()
    {
        Text = text, FontSize = size, Foreground = brush, FontFamily = family ?? SheetFont,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
        HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        VerticalAlignment = center ? VerticalAlignment.Center : VerticalAlignment.Top,
        Margin = margin, MaxWidth = maxWidth,
    };

    private static BitmapSource RenderRegion(Visual visual, Rect region, Brush background, double zoom = 1)
    {
        var size = new Size(region.Width * zoom, region.Height * zoom);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(visual) { Viewbox = region, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, new Rect(size));
        }

        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }

    private static T In<T>(StringTable strings, Func<T> build)
    {
        StringTable.Activate(strings);
        try
        {
            return build();
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>One sheet: a header with its number, title and question, then columns of numbered cards, fitted to 1920 × 1200.</summary>
    private sealed class Sheet(int number, string title, string question)
    {
        private readonly List<List<FrameworkElement>> _columns = [[]];

        public void Add(FrameworkElement item) => _columns[^1].Add(item);

        public void NextColumn() => _columns.Add([]);

        public string Write(string fileName, ITestOutputHelper output)
        {
            var header = new StackPanel { Margin = new Thickness(28, 20, 28, 12) };
            header.Children.Add(Text($"Sheet {number} of 6  ·  {title}", 30, Ink, bold: true));
            header.Children.Add(Text("Question: " + question, 22, FlagInk, margin: new Thickness(0, 8, 0, 0)));
            header.Children.Add(Text("Each item: English as it ships today, then the German draft (green). ⚑ marks a choice to confirm.",
                16, Dim, margin: new Thickness(0, 6, 0, 0)));

            var columns = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var column in _columns)
            {
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
                foreach (var item in column) stack.Children.Add(item);
                columns.Children.Add(stack);
            }

            var body = new Viewbox
            {
                Child = columns, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(28, 0, 14, 14),
            };
            var root = new DockPanel { Width = SheetWidth, Height = SheetHeight, Background = SheetBackground, LastChildFill = true };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(body);

            using var host = FitHost.Open(root, SheetWidth, SheetHeight);
            columns.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var natural = columns.DesiredSize;
            var scale = Math.Min(1, Math.Min(body.ActualWidth / natural.Width, body.ActualHeight / natural.Height));
            output.WriteLine($"{fileName}: content {natural.Width:0}×{natural.Height:0} DIP, drawn at {scale:0.00}");
            return ReviewRender.Save(ReviewRender.Render(root, new Size(SheetWidth, SheetHeight)), Path.Combine(ReviewRender.Directory, fileName));
        }
    }
}
