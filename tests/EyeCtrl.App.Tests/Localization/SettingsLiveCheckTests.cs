namespace EyeCtrl.App.Tests.Localization;

using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Settings;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// T12's supervised live check, Settings: the REAL <see cref="SettingsWindow"/> opens on the real
/// screen with the shipped German, and the HARNESS navigates it — the owner finds steady fixation
/// hard, so no step asks them to click or dwell. Nothing is simulated input: every navigation goes
/// through <c>SettingsWindowViewModel.SimulateActivated</c>, production's own mouse-fallback path,
/// and every step only navigates — tabs, sidebar pages, opening a reset's confirmation modal
/// (never its Yes) and opening the preset picker (never picking a preset).
///
/// <para>
/// <b>Safety.</b> The window runs on a temporary settings file in a temp directory with a
/// GUID-named writer mutex, so no save can reach the owner's <c>%APPDATA%</c>. No gaze input is
/// fed, so nothing dwells on its own.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> The class is <c>Category=UI</c> (CI's <c>Category!=UI</c> filter excludes
/// it) and every test SKIPS unless <c>EYECTRL_LIVE_CHECK=1</c>. Run one case at a time, with the
/// owner's go-ahead:
/// <c>dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~&lt;case&gt;"</c>. The
/// viewer order is the declaration order below; <see cref="LiveCheck.AssertVisible"/> proves each
/// step's window visible, topmost, on-screen and opaque before its hold.
/// </para>
///
/// <para>
/// <b>The Settings combo has no dropdown.</b> A <c>DwellComboBox</c> draws every option as an
/// always-visible pill; nothing in the app opens a popup. The combo case therefore shows a combo
/// row with all its options — the closest thing to "open" the control has.
/// </para>
///
/// <para>
/// <b>The one real-path effect.</b> Every step navigates and nothing acts, so the only thing a
/// stray click by the owner could do is hit "Aufnahmeordner öffnen" or "Anpassungsprotokoll
/// öffnen", which opens Explorer on <c>%APPDATA%\EyeCtrl</c> read-only. Mentioned for the
/// briefing; no file is written.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class SettingsLiveCheckTests
{
    /// <summary>The sidebar and General's first page (the language row): long enough to read the row.</summary>
    private static readonly TimeSpan GeneralHold = TimeSpan.FromSeconds(20);

    /// <summary>One tab's first page in the tour.</summary>
    private static readonly TimeSpan TabHold = TimeSpan.FromSeconds(8);

    /// <summary>The focused steps: the modal, the combo, the ring preview, the picker.</summary>
    private static readonly TimeSpan StepHold = TimeSpan.FromSeconds(15);

    /// <summary>The window's own dwell time for row construction (the value production passes).</summary>
    private const int DwellMs = 700;

    [StaFact]
    public void TheSidebarAndGeneralsFirstPage_ShowTheGermanSettings()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        // The window resolves its sidebar, header and rows from StringTable.Current as it builds,
        // so the shipped German must be current BEFORE Harness.Open().
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            using var harness = Harness.Open();
            // The tray item's own navigation: production's deep link lands on General page 1,
            // where the language row is the first row.
            harness.Settings.NavigateToLanguageRow();
            harness.Settle();
            Check.That(harness.Settings.HeaderTitle.Text).IsEqualTo("Allgemein");
            LiveCheck.AssertShowsText(harness.Settings, "SettingsWindow (General, the language row)",
                "Allgemein", "Sprache / Language");
            LiveCheck.AssertVisible(harness.Settings, "SettingsWindow (General, the language row)");
            LiveCheck.PumpFor(GeneralHold);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void EveryOtherTabsFirstPage_ShowsGerman()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            using var harness = Harness.Open();
            var tabs = LanguageRowReachTests.TabIdsFromSource();
            var reach = LanguageRowReachTests.ToolbarToLanguageRow();
            var pageCount = SettingsTabPagination.PageCount(tabs.Count, reach.PerPage);

            // Sidebar page by sidebar page, so the sidebar always shows the tab it is displaying.
            harness.ShowSidebarPage(reach.GeneralPage);
            for (var page = 0; page < pageCount; page++)
            {
                harness.ShowSidebarPage(page);
                var (start, count) = SettingsTabPagination.PageSlice(tabs.Count, reach.PerPage, page);
                foreach (var tab in tabs.Skip(start).Take(count))
                {
                    if (tab == "General") continue;
                    harness.ViewModel.Settings.ShowTab(tab);
                    harness.Settle();

                    var rendered = SettingsTabDriftTests.RenderedTabDefs().Single(t => t.Id == tab);
                    var german = StringTable.Current.T(rendered.LabelKey);
                    Check.WithCustomMessage($"the {tab} tab resolved its English \"{german}\".")
                        .That(german).IsNotEqualTo(rendered.Label);
                    Check.WithCustomMessage($"the {tab} tab's header reads \"{harness.Settings.HeaderTitle.Text}\".")
                        .That(harness.Settings.HeaderTitle.Text).IsEqualTo(german);
                    LiveCheck.AssertShowsText(harness.Settings, $"SettingsWindow ({tab} tab, first page)", german);
                    LiveCheck.AssertVisible(harness.Settings, $"SettingsWindow ({tab} tab, first page)");
                    LiveCheck.PumpFor(TabHold);
                }
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void ATabReset_ShowsTheGermanConfirmationModal()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            using var harness = Harness.Open();
            // The Dwell tab's reset: its label is one of the two the ticket chose wording for.
            harness.ViewModel.Settings.ShowTab("Dwell");
            harness.Settle();
            harness.ViewModel.Settings.CurrentPage = PageHolding("d_reset", ctx => new DwellPageSource(ctx));
            harness.Settle();
            harness.ActivateRow("d_reset");
            harness.Settle();

            Check.WithCustomMessage("the reset's modal did not open.")
                .That(harness.ViewModel.IsModalVisible).IsTrue();
            // The modal's own question, on screen — not just the view model's flag.
            LiveCheck.AssertShowsText(harness.Settings, "SettingsWindow (the Dwell reset modal)",
                "Einstellungen unter „Verweilzeiten“ auf Standardwerte zurücksetzen?");
            LiveCheck.AssertVisible(harness.Settings, "SettingsWindow (the Dwell reset modal)");
            LiveCheck.PumpFor(StepHold);

            // Closed by code — never the Yes, which would reset the tab.
            harness.ViewModel.HideModal();
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void TheSelectionModeCombo_ShowsItsGermanOptions()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            using var harness = Harness.Open();
            // The Selection tab's mode row: a DwellComboBox, so its options are the always-visible
            // pills — there is no dropdown to open (see the class remarks).
            harness.ViewModel.Settings.ShowTab("Selection");
            harness.Settle();
            var row = new SelectionPageSource(TestSettingsPageContext.Build()).Build()
                .SelectMany(p => p.Rows).OfType<ComboBoxSpec>().Single(c => c.Id == "s_mode");
            Check.That(row.Display).ContainsExactly("Keine", "Manuelle Auswahl", "Automatische Auswahl");
            Check.That(row.Code).ContainsExactly("None", "ManualSelection", "AutoSelection");
            LiveCheck.AssertShowsText(harness.Settings, "SettingsWindow (Selection, the mode combo)",
                "Keine", "Manuelle Auswahl", "Automatische Auswahl");
            LiveCheck.AssertVisible(harness.Settings, "SettingsWindow (Selection, the mode combo)");
            LiveCheck.PumpFor(StepHold);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void TheActionMenuPagesRingPreview_ShowsGerman()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            using var harness = Harness.Open();
            harness.ViewModel.Settings.ShowTab("Actions");
            harness.Settle();
            // The tab's own jump row, which production's dwell would use — reached on whichever
            // page the production chunker put it, computed the same way the window computes it.
            harness.ViewModel.Settings.CurrentPage =
                PageHolding(ActionMenuPageSource.RingJumpRowId, ctx => new ActionMenuPageSource(ctx));
            harness.Settle();
            harness.ActivateRow(ActionMenuPageSource.RingJumpRowId);
            harness.Settle();
            Check.WithCustomMessage("the jump row did not land on the preview page.")
                .That(harness.ViewModel.Settings.CurrentPage)
                .IsEqualTo(harness.ViewModel.Settings.TotalPages - 1);
            // The preview's own German: a painted ring label and its caption.
            LiveCheck.AssertShowsText(harness.Settings, "SettingsWindow (Action Menu ring preview)",
                "Linksklick", "Beschriftungen erscheinen im Betrieb nur beim Verweilen");
            LiveCheck.AssertVisible(harness.Settings, "SettingsWindow (Action Menu ring preview)");
            LiveCheck.PumpFor(StepHold);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void TheQuickActionPicker_ShowsGermanPresetNames()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        ShippedLanguageRecoveryTests.ActivateShippedGerman();
        try
        {
            using var harness = Harness.Open();
            harness.ViewModel.Settings.ShowTab("QuickActions");
            harness.Settle();
            // Add action leads the List's first page (the reachability gate pins that); the type
            // picker and the preset pages are single-view pages, so one activation each reaches
            // the keyboard-shortcut picker. Navigation only: no preset is picked, so nothing is
            // added to the list.
            Check.WithCustomMessage("qa_add is not on QuickActions page 1; the showcase would activate nothing.")
                .That(new QuickActionsPageSource(
                        TestSettingsPageContext.Build(viewModel: QaTestSupport.MakeViewModel()))
                    .Build()[0].Rows.Select(r => r.Id)).Contains("qa_add");
            harness.ActivateRow("qa_add");
            harness.Settle();
            harness.ActivateRow("qa_type_kbd");
            harness.Settle();
            // The picker's German preset names, on screen — the step's target page.
            LiveCheck.AssertShowsText(harness.Settings, "SettingsWindow (quick-action preset picker)",
                "Kopieren", "Einfügen");
            LiveCheck.AssertVisible(harness.Settings, "SettingsWindow (quick-action preset picker)");
            LiveCheck.PumpFor(StepHold);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The 0-based page holding <paramref name="rowId"/> on a tab, computed through the same
    /// production pipeline the window's <c>MaybeRepaginateByHeight</c> runs (the ring preview's
    /// fit, then the height chunker) with the XAML-derived content cell. The showcase's row
    /// activations only work on the page the window has rendered, so the page must be reachable
    /// and the row must be on it — failing loudly here says which. Internal so the headless roster
    /// gate (<c>ShippedGermanSettingsTests.TheLiveChecksRows_AreOnPagesTheShowcaseNavigatesTo</c>)
    /// can prove the same pages without running the showcase.
    /// </summary>
    internal static int PageHolding(string rowId, Func<SettingsPageContext, ISettingsPageSource> sourceFor)
    {
        var root = SettingsContentFitTests.Xml();
        var budget = HandChunkedPageFitTests.ContentHeightBudget(root);
        var width = HandChunkedPageFitTests.ContentColumnWidth(root);
        var pages = SettingsAutoPagination.Repaginate(
            sourceFor(TestSettingsPageContext.Build()).Build().SelectMany(p => p.Rows).ToList(),
            width, budget, SettingsWindow.ControlBtnRadius * 2, DwellMs);
        for (var page = 0; page < pages.Count; page++)
            if (pages[page].Rows.Any(row => row.Id == rowId)) return page;

        throw new InvalidOperationException(
            $"No page of the tab holds \"{rowId}\"; the showcase cannot reach it. This is a layout "
            + "finding: the row the ticket wants shown is on no rendered page.");
    }

    /// <summary>
    /// The real window and everything it needs, pointed away from the owner's <c>%APPDATA%</c>:
    /// a temp settings file under a GUID-named writer mutex, a temp model directory, and no-op
    /// restart/supervision services. Nothing here can write the real settings file.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly ModelTestScope _models;
        private int _sidebarPage;

        private Harness(string root, ModelTestScope models, SettingsWindow settings, SettingsWindowViewModel viewModel)
        {
            Root = root;
            _models = models;
            Settings = settings;
            ViewModel = viewModel;
        }

        public string Root { get; }
        public SettingsWindow Settings { get; }
        public SettingsWindowViewModel ViewModel { get; }

        public static Harness Open()
        {
            var root = Path.Combine(Path.GetTempPath(), "eyectrl_t12_live_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var models = ModelTestFakes.Create();
            try
            {
                var settings = new EyeCtrlSettings();
                var messenger = new WeakReferenceMessenger();
                var service = new SettingsService(
                    Path.Combine(root, "settings.json"), SettingsWriter.App,
                    @"Local\EyeCtrl.Settings.Writer.test_" + Guid.NewGuid().ToString("N"));
                var editor = new SettingsQuickActionsEditorViewModel(
                    new QaTestSupport.FakeQuickActionsStore(), new QaTestSupport.FakeInstalledAppProvider());
                editor.Load();
                var viewModel = new SettingsWindowViewModel(
                    new SettingsViewModel(settings), editor, messenger, settings.Dwell);
                var (registry, management) = models.WithDownloadedBundled();
                var window = new SettingsWindow(
                    viewModel, settings, service, registry, management, messenger,
                    new CalibrationService(), Restart(settings), Supervision(settings));
                window.Show();
                window.Activate();
                LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));
                return new Harness(root, models, window, viewModel);
            }
            catch
            {
                models.Dispose();
                throw;
            }
        }

        /// <summary>One layout and paint pass, so a step's assertions read settled geometry.</summary>
        public void Settle() => LiveCheck.PumpFor(TimeSpan.FromMilliseconds(250));

        public void ActivateRow(string rowId) =>
            ViewModel.SimulateActivated(rowId, DwellCategory.Control);

        /// <summary>
        /// Moves the sidebar one production pager activation at a time, tracking the page the
        /// showcase itself has navigated to (the window owns the real index privately). The move
        /// never activates a boundary direction: from page P to page Q with P != Q the pager id in
        /// that direction is an ordinary step, not the jump-to-end.
        /// </summary>
        public void ShowSidebarPage(int page)
        {
            while (_sidebarPage != page)
            {
                var direction = page > _sidebarPage ? 1 : -1;
                ViewModel.SimulateActivated(
                    direction > 0 ? "tab_next" : "tab_prev", DwellCategory.Pagination);
                _sidebarPage += direction;
                Settle();
            }
        }

        public void Dispose()
        {
            try { Settings.Close(); } catch { /* the window may already be gone */ }
            _models.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        private static AppRestartCoordinator Restart(EyeCtrlSettings settings) =>
            new(messenger: new WeakReferenceMessenger(),
                settings: settings,
                tryReachSupervisor: () => false,
                selfRestart: () => RestartOutcome.Started,
                shutdown: () => { },
                reportInProgress: () => { },
                reportUnavailable: _ => { },
                runInBackground: work => work(),
                postToUi: work => work(),
                supervisorInstalled: () => true,
                bringUp: new SupervisorBringUp(
                    settings, () => { }, () => false, _ => { }, () => { }, () => false));

        private static SupervisionToggleService Supervision(EyeCtrlSettings settings) =>
            new(settings,
                new SupervisorBringUp(
                    settings, () => { }, () => false, _ => { }, () => { }, () => false),
                new SupervisorStartupEnsure(
                    settings, () => { }, () => false, () => false),
                persistEnabled: _ => { },
                supervisorInstalled: () => false,
                isSupervisorConnected: () => false,
                reconcileRunKey: () => { },
                runInBackground: work => work(),
                postToUi: work => work());
    }
}
