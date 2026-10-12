namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Localization;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization.Fit;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.App.Views;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T13: the Step-3 switch-on — calibration, eye care and the overlays — gated on the SHIPPED
/// manifest and table (the production loader against the file beside this test's exe), not on
/// <see cref="GermanDraft"/>'s test manifest. The gates here reuse T09/T10's own measurers: the
/// per-class fit (<see cref="AppFitMeasurers"/>, the same measure GermanFitTests runs on the
/// draft) and the overlay geometry is T09's <see cref="GeometryInvarianceTests"/>, which takes the
/// shipped table as its fourth comparison.
///
/// <para>
/// <b>Zero missing or stale keys</b> is the Python validator's CI step (<c>tools/strings_validate.py</c>)
/// once these three surfaces are in the manifest; the runtime side — a key of a manifest surface
/// with no German — is the shipped table's load warning, asserted empty here.
/// </para>
///
/// <para>
/// <b>B94x2:</b> the ticket expected the eye-care buttons to be classed B94x2. They are not — the
/// banner's start/snooze rings and the exercise's skip button are class A56, measured here, and
/// B94x2 is the "default 100×100 DwellButton" class that carries <b>no keys</b> in the shipped
/// table. A B94x2 case would measure nothing (T11's lesson: no gate that cannot fail), so the A56
/// case is the eye-care buttons' gate.
/// </para>
///
/// <para>
/// The windows and view models here read <see cref="StringTable.Current"/>, so the shipped German is
/// activated globally: the exclusive "StringTable Tests (App)" collection, with English restored
/// after each test. Crises assert literals; so do these, per the story's convention.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class ShippedGermanStep3Tests
{
    private readonly ITestOutputHelper _output;

    public ShippedGermanStep3Tests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The three surfaces' German at their production seams, on the shipped table: the calibration
    /// window's progress line and failure sentence, the eye-care banner's and exercise's captions,
    /// and the live ring's labels as the view model resolves them. A surface that failed to switch
    /// on resolves English, so every seam is pinned as a German literal.
    /// </summary>
    [StaFact]
    public void TheStep3Surfaces_InTheShippedGerman_ResolveGermanAtTheirOwnSeams()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            ShippedLanguageRecoveryTests.ActivateShippedGerman();

            Check.That(CalibrationWindow.ProgressTextFor(
                    CalibrationState.Collecting, index: 0, total: 9, measureOnly: false))
                .IsEqualTo("Auf den Punkt blicken · 1 / 9");
            Check.That(CalibrationWindow.ProgressTextFor(
                    CalibrationState.Training, index: 0, total: 9, measureOnly: true))
                .IsEqualTo("Aufnahme läuft");
            Check.WithCustomMessage("The calibration failure line resolved its English: the calibration surface is not effective.")
                .That(CalibrationFailureText.Describe(QualityFailure(1)))
                .StartsWith("Kalibrierung nicht übernommen");

            var banner = new EyeCareBannerWindow();
            Check.That(TextOf(banner, "SnoozeLabel")).IsEqualTo("später");
            Check.That(TextOf(banner, "StartLabel")).IsEqualTo("starten");
            Check.That(TextOf(banner, "SubText")).IsEqualTo("Sie schauen seit einer Weile auf den Bildschirm");

            var exercise = new EyeCareExerciseWindow();
            Check.That(((Button)exercise.FindName("SkipButton")!).Content).IsEqualTo("Ende ✕");
            Check.That(TextOf(exercise, "SummaryTitle")).IsEqualTo("✓ Gut gemacht");

            using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
            var centre = new PhysicalPoint(1800, 1100);
            vm.Receive(new ShowActionMenuMessage(centre, Enum.GetValues<InteractionAction>().ToList(),
                ShowBack: true, DwellTimeMs: 600, centre, CircleSize: 250));
            Check.That(vm.Items.Select(i => i.Label)).Contains("Linksklick", "2× Klick", "Mehr", "Zurück");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// Binding: every key of the Step-3 surfaces' fit classes, resolved through the SHIPPED table and
    /// measured in its class's production geometry at 2.0, in every theme — T10's German fit gate tied
    /// to the shipped manifest. The classes are the ticket's <b>A105</b> (every ring label),
    /// <b>A68</b> (the modifier bar) and <b>A56</b> (the eye-care captions and buttons); see the
    /// class remark for why B94x2 carries no case.
    /// </summary>
    public static TheoryData<string, string> ThemesAndStep3Classes()
    {
        var data = new TheoryData<string, string>();
        foreach (var theme in ThemeRegistry.Names)
            foreach (var fitClass in new[] { "A105", "A68", "A56" })
                data.Add(theme, fitClass);
        return data;
    }

    [StaTheory]
    [MemberData(nameof(ThemesAndStep3Classes))]
    public void EveryStep3FitClass_InTheShippedGerman_FitsItsClass(string theme, string fitClass)
    {
        var table = ShippedGerman.Load();
        var english = StringTable.EmbeddedEnglish;
        var items = FitCatalog.ItemsOf(fitClass);
        Check.WithCustomMessage($"{fitClass} has no keys: the shipped gate would measure nothing.")
            .That(items).Not.IsEmpty();

        using var scope = ThemeScope.Activate(ThemeRegistry.Resolve(theme));
        var measure = AppFitMeasurers.Measurers[fitClass];
        var failures = new List<string>();
        var translated = 0;
        foreach (var item in items)
        {
            Check.WithCustomMessage(
                    $"{item.Key} carries a plural form; this gate resolves one key per item — expand the "
                    + "forms first instead of measuring one form as the whole key.")
                .That(item.Form).IsEmpty();

            var text = table.T(item.Key);
            if (text != english.T(item.Key)) translated++;
            foreach (var sample in AppFitMeasurers.Samples(item with { Text = text }))
            {
                var verdict = measure(item.Key, sample);
                _output.WriteLine($"{theme} {fitClass} {item.Key} \"{sample}\": {verdict}");
                if (!verdict.Fits) failures.Add($"{item.Key} \"{sample}\": {verdict}");
            }
        }

        Check.WithCustomMessage($"Every {fitClass} text read its English: the shipped German is not effective.")
            .That(translated).IsStrictlyGreaterThan(0);
        Check.WithCustomMessage($"Shipped German texts of {fitClass} do not fit at 2.0 under {theme}:\n  "
                + string.Join("\n  ", failures))
            .That(failures).IsEmpty();
    }

    /// <summary>
    /// The accessible names under the shipped German: every icon-only Step-3 target publishes its
    /// German UIA name, not its English and not a raw glyph — the edge buttons
    /// (<see cref="EdgeButtonFitTests.IconOnlyEdgeButtons_ExposeTheirUiaName"/>'s German half), the
    /// glyph buttons, the backdrop's hide button, the pinned note's ✕ ✎ ↕, the quick-actions grid's
    /// close glyph, and the chromeless pause window's title. German literals on the expected side.
    /// </summary>
    [StaFact]
    public void EveryIconOnlyTarget_InTheShippedGerman_PublishesItsGermanName()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            ShippedLanguageRecoveryTests.ActivateShippedGerman();
            var problems = new List<string>();

            void Expect(string id, string expected, string? actual)
            {
                if (actual != expected) problems.Add($"{id}: UIA name \"{actual}\", expected \"{expected}\"");
            }

            const int dwell = 600;
            var actionMenu = ActionMenuEdgeButtons.Definitions(dwell);
            var keyboard = KeyboardEdgeButtons.Definitions(dwell);
            var manual = ManualSelectionEdgeButtons.Definitions(dwell);
            Expect("ActionMenu Close", "Schließen",
                AnchorName(actionMenu.Single(d => d.Id == ActionMenuEdgeButtons.CloseId)));
            Expect("ActionMenu Help", "Hilfe",
                AnchorName(actionMenu.Single(d => d.Id == ActionMenuEdgeButtons.HelpId)));
            Expect("Keyboard Close", "Schließen",
                AnchorName(keyboard.Single(d => d.Id == KeyboardEdgeButtons.CloseId)));
            Expect("Keyboard Flip", "Oben/unten wechseln",
                AnchorName(keyboard.Single(d => d.Id == KeyboardEdgeButtons.FlipId)));
            Expect("ManualSelection Close", "Schließen",
                AnchorName(manual.Single(d => d.Id == ManualSelectionEdgeButtons.CloseId)));
            Expect("ManualSelection Recapture", "Neu erfassen",
                AnchorName(manual.Single(d => d.Id == ManualSelectionEdgeButtons.RecaptureId)));

            Expect("Drag cancel", "Ziehen abbrechen",
                SymbolName(DragCoordinator.CreateCancelButtonDefinition(dwell)));
            Expect("Drag back", "Zurück",
                SymbolName(DragCoordinator.CreateBackButtonDefinition(dwell)));
            Expect("scroll stop", "Scrollen beenden",
                SymbolName(ScrollModeController.CreateScrollStopButtonDefinition(dwell, verticalOffset: 0)));
            Expect("repeat stop", "Wiederholung beenden",
                SymbolName(RepeatModeController.CreateRepeatStopButtonDefinition(dwell, verticalOffset: 0)));

            var backdrop = new GlassBackdropWindow();
            backdrop.DrawHideButton(new PhysicalPoint(600, 400));
            var hideGlyph = ((Canvas)backdrop.FindName("BackdropCanvas")!)
                .Children.OfType<TextBlock>().Single(t => t.Text == "\u2715");
            Expect("backdrop hide", "Schließen", AutomationProperties.GetName(hideGlyph));

            var note = new NoteWindow("t13-note");
            note.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var named = Descendants<TextBlock>(note)
                .Where(t => !string.IsNullOrEmpty(AutomationProperties.GetName(t)))
                .ToDictionary(t => t.Text, AutomationProperties.GetName);
            Expect("note ✕", "Notiz schließen", named["✕"]);
            Expect("note ✎", "Notiz bearbeiten", named["✎"]);
            Expect("note ↕", "Notiz verschieben", named["↕"]);

            var grid = new QuickActionsGridWindow();
            Expect("grid close", "Schließen",
                AutomationProperties.GetName((TextBlock)grid.FindName("CloseGlyph")!));

            Expect("pause title", "EyeCtrl pausiert", new PauseIndicatorWindow().Title);

            Check.WithCustomMessage(
                    "An icon-only Step-3 target does not publish its German name:\n  "
                    + string.Join("\n  ", problems))
                .That(problems).IsEmpty();
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The ring's segments with labels OFF still announce their German name — the label the user
    /// would have seen with labels on, resolved by the same view model the live ring paints. Each
    /// anchor is compared to its own item's label, so a swapped or missing name fails.
    /// </summary>
    [StaFact]
    public void TheRingSegments_WithLabelsOff_InTheShippedGerman_AnnounceTheirGermanLabels()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            ShippedLanguageRecoveryTests.ActivateShippedGerman();
            var settings = new EyeCtrlSettings();
            settings.ActionMenu.PrimaryActions =
                [InteractionAction.LeftClick, InteractionAction.RightClick, InteractionAction.DoubleClick, InteractionAction.Drag];
            using var vm = new ActionMenuViewModel(settings, new WeakReferenceMessenger());
            var centre = new PhysicalPoint(1800, 1100);
            vm.Receive(new ShowActionMenuMessage(centre, Enum.GetValues<InteractionAction>().ToList(),
                ShowBack: true, DwellTimeMs: 600, centre, CircleSize: 250));

            var canvas = new Canvas { Width = 1400, Height = 1400 };
            var canvasCentre = new Point(700, 700);
            var slots = vm.Items.Select(i => new RingPaintSlot(i.Id,
                ActionMenuGeometry.ButtonCanvasCenter(i.Position, centre, canvasCentre),
                i.Icon, i.Label, i.Kind, i.IsDisabled)).ToList();
            var buttons = ActionMenuRingPainter.Paint(canvas, slots, RingPaintOptions.Runtime(showLabels: false));

            var problems = new List<string>();
            foreach (var item in vm.Items)
            {
                var anchor = buttons[item.Id].AutomationAnchor;
                var actual = anchor is null ? null : AutomationProperties.GetName(anchor);
                if (actual != item.Label) problems.Add($"{item.Id}: UIA name \"{actual}\", item label \"{item.Label}\"");
            }

            Check.WithCustomMessage("The ring drew no labels-off anchors: the check would pass vacuously.")
                .That(buttons).Not.IsEmpty();
            Check.WithCustomMessage("A labels-off ring segment does not announce its German label:\n  "
                    + string.Join("\n  ", problems))
                .That(problems).IsEmpty();
            Check.That(vm.Items.Select(i => i.Label)).Contains("Linksklick", "Mehr", "Zurück");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The toolbar and the Settings tab faces under the shipped German: the AutomationIds UI tests
    /// aim at are language-independent — the same id set as the English build — while the UIA names
    /// turn German (and the paused Pause disc announces the German resume). This is the check the
    /// ticket asks for: UI tests locate by AutomationId, and German must not move one.
    /// </summary>
    [StaFact]
    public void TheToolbarsAndTabFaces_InTheShippedGerman_KeepEveryAutomationId()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var english = ToolbarFaces();
        try
        {
            ShippedLanguageRecoveryTests.ActivateShippedGerman();
            var german = ToolbarFaces();

            Check.WithCustomMessage("German changed the toolbar's AutomationId set; FlaUI locates these by id.")
                .That(german.Keys).ContainsExactly(english.Keys);
            Check.That(german["Toolbar_Button_Pause"]).IsEqualTo("Pausieren");
            Check.That(german["Toolbar_Button_SelectionMode"]).IsEqualTo("Auswahlmodus");
            Check.That(german["Toolbar_Button_Keyboard"]).IsEqualTo("Tastatur");
            Check.That(german["Toolbar_Button_Settings"]).IsEqualTo("Einstellungen");
            Check.That(german["Toolbar_Button_TaskSwitcher"]).IsEqualTo("Fenster wechseln");
            Check.That(german["Toolbar_Button_Modes"]).IsEqualTo("Modi");
            Check.That(german["Toolbar_Button_ResetAdaptation"]).IsEqualTo("Anpassung zurücksetzen");

            // The paused face announces the German resume (T08 fixup R3's name-follows-face rule).
            var toolbar = new ToolbarWindow();
            toolbar.Initialize(ToolbarTestHelpers.ButtonSize, 700);
            toolbar.SetPausedState(true);
            var pause = toolbar.ButtonCanvasForTest.Children.OfType<TextBlock>()
                .Single(t => AutomationProperties.GetAutomationId(t) == "Toolbar_Button_Pause");
            Check.That(AutomationProperties.GetName(pause)).IsEqualTo("Fortsetzen");

            // The Settings tab's AutomationId stays literal while its label is German.
            var face = SettingsWindow.BuildTabFace(
                "Actions", "\u2699", StringTable.Current.T("settings.tab.quickactions.label"),
                new SolidColorBrush(Colors.Transparent));
            Check.WithCustomMessage("The German tab face no longer carries its resolved German label.")
                .That(Descendants<TextBlock>(face).Select(t => t.Text)).Contains("Kurzbefehle");
            var tabAnchor = Descendants<TextBlock>(face)
                .Single(t => !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(t)));
            Check.That(AutomationProperties.GetAutomationId(tabAnchor)).IsEqualTo("Settings_Tab_Actions");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// MEASUREMENT ONLY (T13, the user's decision): the A105 ring's ink clearance for today's
    /// English "Double click", the German ring's "2× Klick" and the Settings row's "Doppelklick",
    /// each measured in the real runtime ring at 2.0 against the +2 DIP rule. No threshold and no
    /// text is decided or changed here: the numbers are logged for the owner's call among keeping
    /// the recorded English exception, lowering the threshold, or rewording the English. The
    /// assertion is only that all three candidates are drawn with a label and stay inside the arc.
    /// </summary>
    [StaFact]
    public void TheDoubleClickCandidates_AreMeasured_InTheRing_ForTheOwnersDecision()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            ShippedLanguageRecoveryTests.ActivateShippedGerman();
            var table = StringTable.Current;
            _output.WriteLine($"The +2 DIP rule: ArcLabelFit.RequiredClearanceDip = {ArcLabelFit.RequiredClearanceDip}");
            var measure = AppFitMeasurers.Measurers["A105"];
            foreach (var (language, text) in new[]
                     {
                         ("English (shipped today)", "Double click"),
                         ("German ring", table.T("overlays.ring.doubleClick")),
                         ("German Settings row word", "Doppelklick"),
                     })
            {
                var verdict = measure("overlays.ring.doubleClick", text);
                var clearance = verdict.Margin + ArcLabelFit.RequiredClearanceDip;
                _output.WriteLine($"{language} \"{text}\": {verdict}");
                _output.WriteLine($"  ink clearance {clearance:0.##} DIP (rule +2; margin {verdict.Margin:0.##})");
                Check.WithCustomMessage($"{language} \"{text}\" is not drawn with a label inside the arc: {verdict}")
                    .That(clearance).IsStrictlyGreaterThan(0.0);
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The D3 window's grid half under the shipped German: the quick-actions grid resolves its preset
    /// tiles through <see cref="OverlayTextSources"/> onto the <c>overlays</c> surface, so now that the
    /// surface is switched on the grid reads the same German the Settings picker has shown since T12 —
    /// the pairing <see cref="MirrorParityTests"/> keeps identical. Pinned literals plus a
    /// German-vs-English count, so an all-English grid cannot pass.
    /// </summary>
    [Fact]
    public void TheQuickActionsGrid_InTheShippedGerman_ShowsTheGermanPresetNames()
    {
        try
        {
            ShippedLanguageRecoveryTests.ActivateShippedGerman();
            var presets = QuickActionPresets.KeyboardShortcuts
                .Concat(QuickActionPresets.MediaControls)
                .Concat(QuickActionPresets.WindowActions)
                .ToList();
            var translated = 0;
            foreach (var preset in presets)
            {
                var label = OverlayTextSources.PresetLabelFor(preset.Id);
                Check.WithCustomMessage($"the grid has no overlays label for {preset.Id}.").That(label).IsNotNull();
                if (label != preset.Label) translated++;
            }

            Check.That(presets.Count).IsStrictlyGreaterThan(20);
            Check.WithCustomMessage(
                    "A grid tile still reads its preset's English: the overlays surface is not effective "
                    + "for the grid.")
                .That(translated).IsEqualTo(presets.Count);
            Check.That(OverlayTextSources.PresetLabelFor("kbd.copy")).IsEqualTo("Kopieren");
            Check.That(OverlayTextSources.PresetLabelFor("media.play_pause")).IsEqualTo("Wiedergabe / Pause");
            Check.That(OverlayTextSources.PresetLabelFor("window.minimize")).IsEqualTo("Fenster minimieren");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    // ────── helpers ──────

    /// <summary>The toolbar anchor ids and their UIA names, built from the currently active table.</summary>
    private static Dictionary<string, string> ToolbarFaces()
    {
        var window = new ToolbarWindow();
        window.Initialize(ToolbarTestHelpers.ButtonSize, 700);
        return window.ButtonCanvasForTest.Children.OfType<TextBlock>()
            .Where(t => !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(t)))
            .ToDictionary(AutomationProperties.GetAutomationId, AutomationProperties.GetName);
    }

    private static string? AnchorName(EdgeButtonDefinition definition)
    {
        var (button, _) = ArcLabelFit.BuildButton(definition);
        return button.AutomationAnchor is { } anchor ? AutomationProperties.GetName(anchor) : null;
    }

    private static string? SymbolName(EdgeButtonDefinition definition)
    {
        var (button, _) = ArcLabelFit.BuildButton(definition);
        return button.Symbol is { } symbol ? AutomationProperties.GetName(symbol) : null;
    }

    private static string TextOf(FrameworkElement window, string name) =>
        ((TextBlock)window.FindName(name)!).Text;

    /// <summary>
    /// The descendants of <paramref name="root"/> in the LOGICAL tree. The overlay windows here are
    /// constructed, never shown, so their visual tree is not built; the logical tree is what their
    /// drawn children hang from before the first layout pass (the walker NoteWindowNameTests uses).
    /// </summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is T match) yield return match;
            foreach (var descendant in Descendants<T>(node)) yield return descendant;
        }
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
}
