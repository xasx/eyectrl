namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Localization.Fit;
using EyeCtrl.App.Tests.Settings;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// T12: the Step-2 switch-on (Settings), gated on the SHIPPED manifest and table — the production
/// loader against the file beside this test's exe, not <see cref="GermanDraft"/>'s test manifest.
/// The binding gates reuse T09/T10's own sweeps and measurers with the shipped table: the
/// pagination reachability sweep (<see cref="HandChunkedPageFitTests.AssertEveryPageReachable"/>),
/// the tab156 sidebar sweep (<see cref="GermanGateTests.AssertEverySidebarTabKeepsItsGeometry"/>),
/// the B260x2 button measurer (<see cref="AppFitMeasurers"/>), T04's language-row reach
/// (<see cref="LanguageRowReachTests.ToolbarToLanguageRow"/>), T03a's combo-code guard
/// (<see cref="ComboCodeIdentityTests.WalkableCombos"/>) and the D2 preset-label sites.
///
/// <para>
/// A surface that failed to switch on resolves English without throwing, so every test here pins
/// at least one German <b>literal</b> or a German-vs-English difference at the site it measures.
/// The German is user-approved (T10): if a gate fails on text, the text does not change.
/// </para>
///
/// <para>
/// The page sources read <see cref="StringTable.Current"/>, so the tests that build them activate
/// the shipped table globally: the exclusive "StringTable Tests (App)" collection, with English
/// restored after each. The tests that measure a passed-in table need no activation.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class ShippedGermanSettingsTests
{
    /// <summary>
    /// Binding: under the SHIPPED German every Settings page paginates and every row stays
    /// reachable — T10's sweep on the shipped table instead of the all-surfaces test manifest.
    /// The sweep also fails a page left with no row and a page whose dwell circles leave the
    /// content cell, which is the "no page is empty" half of the ticket's gate.
    /// </summary>
    [StaFact]
    public void EverySettingsPage_InTheShippedGerman_PaginatesAndStaysReachable()
    {
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            Check.WithCustomMessage("The shipped table is not German for Settings: the sweep would measure English.")
                .That(StringTable.Current.T("settings.tab.general.label")).IsEqualTo("Allgemein");
            HandChunkedPageFitTests.AssertEveryPageReachable("shipped German (T12)");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// Binding: the tab156 gate on the shipped German, through T10's own sweep. The two labels the
    /// ticket chose wording for are pinned as literals; the sweep proves every tab stays on one
    /// line, keeps the sidebar's pitch and holds its English box.
    /// </summary>
    [StaFact]
    public void TheSidebar_InTheShippedGerman_KeepsEveryTabWhereItWas_AndPinsTheTwoLongestLabels()
    {
        var table = ShippedGerman.Load();
        Check.That(table.T("settings.tab.quickactions.label")).IsEqualTo("Kurzbefehle");
        Check.That(table.T("settings.tab.eyecare.label")).IsEqualTo("Augenschutz");

        GermanGateTests.AssertEverySidebarTabKeepsItsGeometry(table, "shipped German");
    }

    /// <summary>
    /// Binding: every German button of the fixed 300×100 box (<c>B260x2</c>) shows whole in
    /// production's button, with its text resolved THROUGH the shipped table — a manifest omission
    /// would leave the English in place and fail the German-vs-English check. The two labels the
    /// ticket chose wording for are pinned as literals.
    /// </summary>
    [StaFact]
    public void EveryB260x2Button_InTheShippedGerman_FitsItsBox_AndPinsTheTwoLongestLabels()
    {
        var table = ShippedGerman.Load();
        var english = StringTable.EmbeddedEnglish;
        var items = FitCatalog.ItemsOf("B260x2");
        Check.WithCustomMessage("No B260x2 keys were found: the gate would pass vacuously.")
            .That(items).Not.IsEmpty();

        Check.That(table.T("settings.dwell.reset.label")).IsEqualTo("„Verweilzeiten“ zurücksetzen");
        Check.That(table.T("settings.general.resetall.label")).IsEqualTo("⚠ ALLE Einstellungen zurücksetzen");

        var measure = AppFitMeasurers.Measurers["B260x2"];
        var failures = new List<string>();
        foreach (var item in items)
        {
            Check.WithCustomMessage(
                    $"{item.Key} carries a plural form; this gate has no form rule — teach it the "
                    + "composed texts instead of measuring one form as the whole key.")
                .That(item.Form).IsEmpty();

            var german = table.T(item.Key);
            Check.WithCustomMessage(
                    $"{item.Key} resolved its English \"{german}\" under the shipped manifest: the "
                    + "settings surface is not effective, or this key has no German.")
                .That(german).IsNotEqualTo(english.T(item.Key));

            var verdict = measure(item.Key, german);
            if (!verdict.Fits) failures.Add($"{item.Key} \"{german}\": {verdict}");
        }

        Check.WithCustomMessage("German B260x2 buttons overrun their 300×100 box:\n  " + string.Join("\n  ", failures))
            .That(failures).IsEmpty();
    }

    /// <summary>
    /// T03a's code guard under the shipped German: every combo's <c>Code</c> array is identical to
    /// the English build's, so a German session saves exactly what an English one does — including
    /// the language row, which must keep saving the JSON token ("german"), never the endonym
    /// ("Deutsch"). The display side must actually be German, so the gate is not comparing two
    /// English builds.
    /// </summary>
    [StaFact]
    public void EveryCombo_SavesTheSameCodes_UnderTheShippedGerman()
    {
        using var models = ModelTestFakes.Create();
        var (registry, management) = models.WithDownloadedBundled();
        var english = WalkableCombos(new EyeCtrlSettings(), registry, management);

        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            var german = WalkableCombos(new EyeCtrlSettings(), registry, management);
            Check.WithCustomMessage(
                    $"expected {english.Count} combos but walked {german.Count}; a combo added or "
                    + "removed must be added to both walks.")
                .That(german.Keys).HasSize(english.Count);

            var translated = 0;
            foreach (var (id, baseline) in english)
            {
                Check.That(german).ContainsKey(id);
                Check.WithCustomMessage(
                        $"{id}: the code array changed under German, so a German session would "
                        + "save different values than an English one.")
                    .That(german[id].Code).ContainsExactly(baseline.Code);
                if (german[id].Display != baseline.Display) translated++;
            }

            Check.WithCustomMessage("Every combo resolved its English: the shipped German is not effective.")
                .That(translated).IsStrictlyGreaterThan(5);

            Check.That(german["g_language"].Code).ContainsExactly("system", "german", "english");
            Check.That(german["g_language"].Display).ContainsExactly("System", "Deutsch", "English");
            Check.That(german["c_filter"].Display)
                .ContainsExactly("Direkt", "Geglättet", "Geglättet (alt)", "One Euro");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// D2's read sites under the shipped German: the list caption and the delete modal show the
    /// German preset name rather than the tile's stale stored label.
    /// </summary>
    [Fact]
    public void ThePresetListCaptionAndDeleteModal_InTheShippedGerman_ShowTheGermanName()
    {
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            var rec = new RecordingSettingsPageContext(
                viewModel: QaTestSupport.MakeViewModel(
                    QuickActionsLabelRouteTests.StoreWith(QuickActionsLabelRouteTests.StalePresetTile())));
            var list = new QuickActionsPageSource(rec.Context);
            var rows = list.Build().SelectMany(p => p.Rows).ToList();

            var caption = rows.OfType<ToggleSpec>().Single(t => t.Id.StartsWith("qa_en_")).Label;
            Check.WithCustomMessage($"the list caption reads \"{caption}\".")
                .That(caption).Contains("Kopieren");
            Check.That(caption).Not.Contains("Kopie (veraltet)");

            var del = rows.OfType<ButtonSpec>().Single(b => b.Id.StartsWith("qa_del_"));
            del.OnActivate();
            Check.That(rec.LastModalMessage).IsEqualTo("„Kopieren“ löschen?");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// D2's add path under the shipped German, one case per preset family (review-01 F3): the
    /// picker button shows the German name, and picking it still stores the preset's ENGLISH label
    /// plus its <see cref="EyeCtrl.Core.Models.QuickAction.PresetId"/> — the grid stays English
    /// until Step 3 while the picker speaks German. In English the German-storing mutant is
    /// equivalent, so only this German theory can kill it; the view ids and preset data mirror the
    /// English theory in <see cref="QuickActionsLabelRouteTests"/>.
    /// </summary>
    [Theory]
    [InlineData("PickShortcut", "qa_ks_0", QuickActionType.KeyboardShortcut, "kbd.copy", "Kopieren", "Copy", "Ctrl+C")]
    [InlineData("PickMedia", "qa_md_0", QuickActionType.MediaControl, "media.play_pause", "Wiedergabe / Pause", "Play / Pause", "PlayPause")]
    [InlineData("PickWindowAction", "qa_win_0", QuickActionType.WindowAction, "window.minimize", "Fenster minimieren", "Minimize Window", "Minimize")]
    public void EveryPresetPicker_InTheShippedGerman_ShowsGermanNames_AndStoresThePresetId(
        string viewName, string rowId, QuickActionType expectedType,
        string expectedPresetId, string expectedGerman, string expectedEnglish, string expectedPayload)
    {
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            var picker = new RecordingSettingsPageContext(
                viewModel: QaTestSupport.MakeViewModel(QuickActionsLabelRouteTests.StoreWith()));
            var source = new QuickActionsPageSource(picker.Context);
            source.GoTo(Enum.Parse<QuickActionsPageSource.QaView>(viewName));

            var add = source.Build().First().Rows.OfType<ButtonSpec>().Single(b => b.Id == rowId);
            Check.WithCustomMessage($"{viewName}: the picker button reads \"{add.Label}\".")
                .That(add.Label).IsEqualTo(expectedGerman);

            add.OnActivate();
            var editor = picker.Context.ViewModel.QuickActionsEditor;
            Check.That(editor.Actions).HasSize(1);
            Check.That(editor.Actions[0].Type).IsEqualTo(expectedType);
            Check.That(editor.Actions[0].PresetId).IsEqualTo(expectedPresetId);
            Check.WithCustomMessage(
                    $"{viewName}: the picker stored the German display label; the tile must keep "
                    + "the preset's English so the grid stays readable until Step 3 decides its German.")
                .That(editor.Actions[0].Label).IsEqualTo(expectedEnglish);
            Check.That(editor.Actions[0].Payload).IsEqualTo(expectedPayload);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// T04's reach pin under the shipped German: the dwell count from the toolbar is still 3, and
    /// the row that count assumes — the language row first on General page 1 — is still the first
    /// rendered row after German re-pagination. A German page that pushed the recovery row behind
    /// another page would break the count while the arithmetic alone stayed green.
    /// </summary>
    [StaFact]
    public void FromTheToolbarSettingsButton_TheLanguageRow_IsStillThreeDwellsAway_InTheShippedGerman()
    {
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            Check.WithCustomMessage("The shipped table is not German for Settings: the reach would be measured on English.")
                .That(StringTable.Current.T("settings.tab.general.label")).IsEqualTo("Allgemein");

            var reach = LanguageRowReachTests.ToolbarToLanguageRow();
            Check.WithCustomMessage(
                    "The German re-pagination moved the reach pin; the language row is a recovery "
                    + "row, so a longer path is a UX decision, not a test failure to paper over.")
                .That(reach.Dwells).IsEqualTo(LanguageRowReachTests.PinnedToolbarToLanguageRowDwells);
            Check.That(reach.PerPage).IsEqualTo(6);
            Check.That(reach.GeneralPage).IsEqualTo(1);

            Check.WithCustomMessage(
                    "Under the shipped German the language row is not the first row of General page 1, "
                    + "so the pinned 3-dwell path no longer lands on it.")
                .That(LanguageRowReachTests.FirstRenderedRowId()).IsEqualTo("g_language");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The live check's navigation roster, headless (review-01 F2's teeth): every row the
    /// showcase activates — and the picker page it lands on — is one the production page pipeline
    /// renders for its tab, so a step cannot silently no-op. The on-screen half of the proof (the
    /// step's German is actually visible) is <c>LiveCheck.AssertShowsText</c>, which only the live
    /// check can run; this half fails headlessly when a showcased row id moves or disappears.
    /// </summary>
    [StaFact]
    public void TheLiveChecksRows_AreOnPagesTheShowcaseNavigatesTo()
    {
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            Check.ThatCode(() => SettingsLiveCheckTests.PageHolding(
                    "d_reset", ctx => new DwellPageSource(ctx)))
                .DoesNotThrow();
            Check.ThatCode(() => SettingsLiveCheckTests.PageHolding(
                    "s_mode", ctx => new SelectionPageSource(ctx)))
                .DoesNotThrow();
            Check.ThatCode(() => SettingsLiveCheckTests.PageHolding(
                    ActionMenuPageSource.RingJumpRowId, ctx => new ActionMenuPageSource(ctx)))
                .DoesNotThrow();

            var picker = new RecordingSettingsPageContext(
                viewModel: QaTestSupport.MakeViewModel(QuickActionsLabelRouteTests.StoreWith()));
            var source = new QuickActionsPageSource(picker.Context);
            Check.WithCustomMessage("the showcase's qa_add is not on QuickActions page 1.")
                .That(source.Build()[0].Rows.Select(r => r.Id)).Contains("qa_add");
            source.GoTo(QuickActionsPageSource.QaView.PickType);
            Check.WithCustomMessage("the showcase's qa_type_kbd is not on the type picker's page.")
                .That(source.Build()[0].Rows.Select(r => r.Id)).Contains("qa_type_kbd");
            source.GoTo(QuickActionsPageSource.QaView.PickShortcut);
            Check.WithCustomMessage("the showcase's keyboard picker's first page holds no preset.")
                .That(source.Build()[0].Rows.Select(r => r.Id)).Contains("qa_ks_0");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>Every combo the two walks compare: the walkable sources plus Models' md_active.</summary>
    private static Dictionary<string, ComboBoxSpec> WalkableCombos(
        EyeCtrlSettings settings, ModelRegistryService registry, ModelManagementService management)
    {
        var combos = ComboCodeIdentityTests.WalkableCombos(settings).ToDictionary(c => c.Id);
        combos["md_active"] = CombosOf(new ModelsPageSource(
            TestSettingsPageContext.Build(registry: registry, modelManagement: management))).Single();
        return combos;
    }

    private static IEnumerable<ComboBoxSpec> CombosOf(ISettingsPageSource source) =>
        source.Build().SelectMany(p => p.Rows).OfType<ComboBoxSpec>();
}
