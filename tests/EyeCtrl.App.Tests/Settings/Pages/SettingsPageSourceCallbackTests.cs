namespace EyeCtrl.App.Tests.Settings.Pages;

using System;
using System.Linq;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;
using NFluent;
using Xunit;

/// <summary>
/// Exercises the load-bearing callbacks the structural page-source tests sample but never invoke.
/// <see cref="TestSettingsPageContext"/> hard-wires <c>ApplyAndSave</c>/<c>RebuildContent</c>/
/// <c>Invoke</c>/<c>Close</c> to no-ops, which is enough to assert row IDs and attributes but
/// cannot verify the reset field-copy lists, cal_run ordering, model-deletion path, or QA state
/// transitions this refactor transcribed. Each test here drives a real <see cref="EyeCtrlSettings"/>
/// through the page source's <c>OnActivate</c> callbacks via a <see cref="RecordingSettingsPageContext"/>.
/// </summary>
public class SettingsPageSourceCallbackTests
{
    // 1. KeyboardPageSource reset — the field-copy list is the transcription's load-bearing detail.
    // It restores exactly the settings that have a ROW on the Keyboard tab (completeness is
    // guarded by KeyboardPageSourceResetDriftTests, which enumerates the rows — this test no
    // longer hand-lists them; the list here had already drifted twice: MaxSuggestions joined the
    // handler in #160 unnoticed, and ClickSoundEnabled was the miss that filed #157) and must
    // NOT touch anything else — SmartCapitalizationEnabled is the canary, chosen because it has
    // no row on the tab: set false before reset, must stay false, proving the reset did not
    // wholesale-replace the Keyboard object. Base: 385211f lines 796-808.
    [Fact]
    public void Keyboard_Reset_OnConfirmedModal_ClearsLanguagePack_AndLeavesRowlessFieldsAlone()
    {
        var settings = new EyeCtrlSettings();
        var k = settings.Keyboard;
        k.LanguagePack = "fr";
        k.SmartCapitalizationEnabled = false; // canary — no row on the tab, must survive untouched
        k.ActiveLayout = "AZERTY";
        k.KeySizeMultiplier = 1.5;
        k.PredictionEnabled = false;
        k.ScreenEdge = "Top";

        var rec = new RecordingSettingsPageContext(settings);
        var reset = new KeyboardPageSource(rec.Context).Build()[0].Rows
            .OfType<ButtonSpec>().Single(b => b.Id == "k_reset");

        // OnActivate shows the confirm modal but does NOT apply anything until the user says Yes.
        reset.OnActivate();
        Check.That(rec.LastModalMessage ?? "").Contains("Reset Keyboard");
        Check.That(rec.LastModalOnYes).IsNotNull();
        Check.That(rec.ApplyAndSaveCount).IsEqualTo(0);

        rec.LastModalOnYes!();                // simulate the user confirming

        var defaults = new KeyboardSettings();
        Check.That(k.LanguagePack).IsEqualTo(defaults.LanguagePack);
        Check.That(k.ActiveLayout).IsEqualTo(defaults.ActiveLayout);
        Check.That(k.KeySizeMultiplier).IsEqualTo(defaults.KeySizeMultiplier);
        Check.That(k.PredictionEnabled).IsEqualTo(defaults.PredictionEnabled);
        Check.That(k.ScreenEdge).IsEqualTo(defaults.ScreenEdge);
        Check.That(k.SmartCapitalizationEnabled).IsFalse();   // KEY: untouched, not wholesale-reset
        Check.That(rec.ApplyAndSaveCount).IsEqualTo(1);
        Check.That(rec.RebuildContentCount).IsEqualTo(1);
    }

    // 2. CursorPageSource reset — one sampled field (FilterMode) restored to its default, and the
    // save+rebuild pair fires exactly once.
    [Fact]
    public void Cursor_Reset_OnConfirmedModal_RestoresFilterMode()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;   // default is OneEuro

        var rec = new RecordingSettingsPageContext(settings);
        var reset = new CursorPageSource(rec.Context).Build()[1].Rows
            .OfType<ButtonSpec>().Single(b => b.Id == "c_reset");

        reset.OnActivate();
        rec.LastModalOnYes!();

        Check.That(settings.Cursor.FilterMode).IsEqualTo(new CursorSettings().FilterMode);
        Check.That(rec.ApplyAndSaveCount).IsEqualTo(1);
        Check.That(rec.RebuildContentCount).IsEqualTo(1);
    }

    // 3. DwellPageSource reset — one sampled field (FixationDetectionMs) restored to its default.
    [Fact]
    public void Dwell_Reset_OnConfirmedModal_RestoresFixationDetectionMs()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.FixationDetectionMs = 250;          // default is 300

        var rec = new RecordingSettingsPageContext(settings);
        var reset = new DwellPageSource(rec.Context).Build()[1].Rows
            .OfType<ButtonSpec>().Single(b => b.Id == "d_reset");

        reset.OnActivate();
        rec.LastModalOnYes!();

        Check.That(settings.Dwell.FixationDetectionMs).IsEqualTo(new DwellSettings().FixationDetectionMs);
        Check.That(rec.ApplyAndSaveCount).IsEqualTo(1);
        Check.That(rec.RebuildContentCount).IsEqualTo(1);
    }

    // 4. CalibrationPageSource cal_run — the close-then-send ordering is the whole point of the
    // transcription (the window must close before handing off to the calibration overlay). The
    // shared ordered log lets us assert Close precedes the CalibrationRequestedMessage send.
    // Base: CalibrationPageSource.cs:27-30; base 385211f lines 1289-1292.
    [Fact]
    public void Calibration_Run_ClosesBeforeSendingRequest()
    {
        var rec = new RecordingSettingsPageContext();
        var run = new CalibrationPageSource(rec.Context).Build()[1].Rows
            .OfType<ButtonSpec>().Single(b => b.Id == "cal_run");

        run.OnActivate();

        Check.That(rec.CloseCount).IsEqualTo(1);
        Check.That(rec.Log).ContainsExactly(
            RecordingSettingsPageContext.CloseEntry,
            nameof(CalibrationRequestedMessage));
    }

    // 5. ModelsPageSource delete — invoking the delete button captures a confirm modal; confirming
    // runs the delete path (DeleteModel + ApplyAndSave + RebuildContent) and the model is no longer
    // reported as downloaded. Delete rows live in the Manage view (md_manage switches to it), so the
    // test enters that view first. Uses the disposable ModelTestFakes scope so the temp dir is cleaned.
    [Fact]
    public void Models_Delete_OnConfirmedModal_RunsDeletePath()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithDownloadedBundled("135M");
        var rec = new RecordingSettingsPageContext(registry: registry, modelManagement: mgmt);
        var src = new ModelsPageSource(rec.Context);

        src.Build().Single().Rows.OfType<ButtonSpec>().Single(b => b.Id == "md_manage").OnActivate();
        var del = src.Build().Single().Rows
            .OfType<ButtonSpec>().Single(b => b.Id.StartsWith("md_del_"));

        del.OnActivate();
        Check.That(rec.LastModalMessage ?? "").Contains("Delete");
        Check.That(rec.LastModalOnYes).IsNotNull();

        rec.LastModalOnYes!();

        Check.That(rec.ApplyAndSaveCount).IsEqualTo(1);
        // Two rebuilds: the GoTo(Manage) that revealed the delete rows, then the post-delete one.
        Check.That(rec.RebuildContentCount).IsEqualTo(2);
        Check.That(registry.IsModelDownloaded("135M")).IsFalse();
    }

    // 6. QuickActionsPageSource transition — the preset picker's OnActivate adds a keyboard-shortcut
    // action to the editor's store, and a qa_back_* button returns the view to the list page.
    [Fact]
    public void QuickActions_PresetPicker_AddsAction_AndBackReturnsToList()
    {
        var rec = new RecordingSettingsPageContext(viewModel: QaTestSupport.MakeViewModel());
        var editor = rec.Context.ViewModel.QuickActionsEditor;
        var src = new QuickActionsPageSource(rec.Context);

        // Preset add: PickShortcut page → first preset button adds a KeyboardShortcut action.
        src.GoTo(QuickActionsPageSource.QaView.PickShortcut);
        var preset = src.Build().First().Rows.OfType<ButtonSpec>()
            .First(b => !b.Id.StartsWith("qa_back"));
        int before = editor.Actions.Count;
        preset.OnActivate();
        Check.That(editor.Actions.Count).IsStrictlyGreaterThan(before);
        Check.That(editor.Actions[^1].Type).IsEqualTo(QuickActionType.KeyboardShortcut);

        // Back transition: re-enter the picker, invoke qa_back, and Build() must return the LIST
        // page rather than the picker it came from.
        //
        // The two properties are asserted directly — no qa_back button, and the config rows are
        // there — rather than inferred from whichever row happens to be first. This used to read
        // ((ToggleSpec)Rows[0]).Id == "qa_enabled", which coupled a view-transition test to the
        // config page's ROW ORDER and broke the day qa_add moved to the front to get its dwell
        // circle above the fold (issue #113). Row order is QuickActionsPageSourceTests' business;
        // what belongs here is only which view Build() returned.
        src.GoTo(QuickActionsPageSource.QaView.PickShortcut);
        var back = src.Build().First().Rows.OfType<ButtonSpec>()
            .First(b => b.Id.StartsWith("qa_back"));
        back.OnActivate();

        var listPage = src.Build().First().Rows;
        Check.That(listPage.OfType<ButtonSpec>().Select(b => b.Id))
            .Not.HasElementThatMatches(id => id.StartsWith("qa_back", StringComparison.Ordinal));
        Check.That(listPage.OfType<ToggleSpec>().Select(t => t.Id)).Contains("qa_enabled");
    }

    // 6b. QuickActionsPageSource landing page — GoTo must reset the page index. The picker's
    // OnActivate callbacks return to the List view; if the picker was left on page 1 (14
    // keyboard presets, 3 per page), the List used to open on its page 1 — the config tail
    // ({Dismiss Timeout, Grid Columns}) — instead of the qa_add page. The recording context's
    // RebuildContent does NOT clamp (only the real window does), so a surviving index is
    // observed raw here.
    [Fact]
    public void QuickActions_PresetAdd_FromPickerPage1_LandsOnTheListLandingPage()
    {
        var rec = new RecordingSettingsPageContext(viewModel: QaTestSupport.MakeViewModel());
        var editor = rec.Context.ViewModel.QuickActionsEditor;
        var pages = rec.Context.ViewModel.Settings;
        var src = new QuickActionsPageSource(rec.Context);

        // Sit on page 1 of the shortcut picker, then add a preset from it — its callback
        // returns to the List view.
        src.GoTo(QuickActionsPageSource.QaView.PickShortcut);
        pages.CurrentPage = 1;
        var preset = src.Build()[1].Rows.OfType<ButtonSpec>()
            .First(b => !b.Id.StartsWith("qa_back"));
        int before = editor.Actions.Count;
        preset.OnActivate();
        Check.That(editor.Actions.Count).IsStrictlyGreaterThan(before);

        // The List must land on page 0, where qa_add lives.
        Check.That(pages.CurrentPage).IsEqualTo(0);
        Check.That(src.Build()[0].Rows.OfType<ButtonSpec>().Select(b => b.Id))
            .Contains("qa_add");
    }

    // 7. DiagnosticsPageSource toggles — each saves and, critically, does NOT rebuild.
    // Deliberately not a copy of the reset test (DiagnosticsPageSourceTests pins that, along with
    // the rest of the page): the distinct claim here is the absence of a RebuildContent. A rebuild
    // tears down and re-creates every row control, which for a gaze user re-registers the dwell
    // targets under their gaze — so a toggle that rebuilt would cancel the dwell that flipped it,
    // and could re-trigger on the row now sitting under the same point. Resets rebuild on purpose
    // (every row's value changed); a single toggle must not.
    [Fact]
    public void Diagnostics_Toggles_SaveWithoutRebuildingThePageUnderTheUsersGaze()
    {
        var settings = new EyeCtrlSettings();
        var rec = new RecordingSettingsPageContext(settings);
        var toggles = new DiagnosticsPageSource(rec.Context).Build()
            .SelectMany(p => p.Rows).OfType<ToggleSpec>().ToDictionary(t => t.Id);

        toggles["dg_record_raw"].Set(true);

        Check.That(settings.Diagnostics.RecordRawGaze).IsTrue();
        Check.That(rec.ApplyAndSaveCount).IsEqualTo(1);
        Check.That(rec.RebuildContentCount).IsEqualTo(0);

        toggles["dg_ui_instruments"].Set(true);

        Check.That(settings.Diagnostics.MeasureUiInstruments).IsTrue();
        Check.That(rec.ApplyAndSaveCount).IsEqualTo(2);
        Check.That(rec.RebuildContentCount).IsEqualTo(0);
    }
}
