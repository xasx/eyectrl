namespace EyeCtrl.App.Tests.Settings.Pages;

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
    // It must clear LanguagePack and restore exactly the four fields the base reset touches
    // (ActiveLayout/KeySizeMultiplier/PredictionEnabled/ScreenEdge) and must NOT touch anything
    // else — ClickSoundEnabled is the canary: it is set false before reset and must stay false,
    // proving the reset did not wholesale-replace the Keyboard object. Base: 385211f lines 796-808.
    [Fact]
    public void Keyboard_Reset_OnConfirmedModal_ClearsLanguagePack_AndRestoresFourFieldsOnly()
    {
        var settings = new EyeCtrlSettings();
        var k = settings.Keyboard;
        k.LanguagePack = "fr";
        k.ClickSoundEnabled = false;          // canary — must survive the reset untouched
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
        Check.That(k.LanguagePack).IsEqualTo("");
        Check.That(k.ActiveLayout).IsEqualTo(defaults.ActiveLayout);
        Check.That(k.KeySizeMultiplier).IsEqualTo(defaults.KeySizeMultiplier);
        Check.That(k.PredictionEnabled).IsEqualTo(defaults.PredictionEnabled);
        Check.That(k.ScreenEdge).IsEqualTo(defaults.ScreenEdge);
        Check.That(k.ClickSoundEnabled).IsFalse();   // KEY: untouched, not wholesale-reset
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
    // reported as downloaded. Uses the disposable ModelTestFakes scope so the temp dir is cleaned.
    [Fact]
    public void Models_Delete_OnConfirmedModal_RunsDeletePath()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithDownloadedBundled("135M");
        var rec = new RecordingSettingsPageContext(registry: registry, modelManagement: mgmt);

        var del = new ModelsPageSource(rec.Context).Build()[1].Rows
            .OfType<ButtonSpec>().Single(b => b.Id.StartsWith("md_del_"));

        del.OnActivate();
        Check.That(rec.LastModalMessage ?? "").Contains("Delete");
        Check.That(rec.LastModalOnYes).IsNotNull();

        rec.LastModalOnYes!();

        Check.That(rec.ApplyAndSaveCount).IsEqualTo(1);
        Check.That(rec.RebuildContentCount).IsEqualTo(1);
        Check.That(registry.IsModelDownloaded("135M")).IsFalse();
    }

    // 6. QuickActionsPageSource transition — the preset picker's OnActivate adds a keyboard-shortcut
    // action to the editor's store, and a qa_back_* button returns the view to the list page.
    [Fact]
    public void QuickActions_PresetPicker_AddsAction_AndBackReturnsToList()
    {
        var rec = new RecordingSettingsPageContext(viewModel: MakeQaViewModel());
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

        // Back transition: re-enter the picker, invoke qa_back, and Build() must return the list
        // page (whose first row is the qa_enabled toggle, not a qa_back button).
        src.GoTo(QuickActionsPageSource.QaView.PickShortcut);
        var back = src.Build().First().Rows.OfType<ButtonSpec>()
            .First(b => b.Id.StartsWith("qa_back"));
        back.OnActivate();
        var firstRow = src.Build().First().Rows[0];
        Check.That(((ToggleSpec)firstRow).Id).IsEqualTo("qa_enabled");
    }

    // Mirrors QuickActionsPageSourceTests.MakeQaViewModel (re-declared because the two fakes there
    // are private nested). The editor needs a store + app provider to construct.
    private static SettingsWindowViewModel MakeQaViewModel()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var settingsService = new SettingsService("test_qa_callback_" + Guid.NewGuid() + ".json");
        var settingsVM = new SettingsViewModel(settings, settingsService, messenger);
        var editor = new SettingsQuickActionsEditorViewModel(
            new FakeQuickActionsStore(), new FakeInstalledAppProvider());
        var vm = new SettingsWindowViewModel(settingsVM, editor, messenger, settings.Dwell);
        editor.Load();   // production calls Load() right after constructing the editor
        return vm;
    }

    private sealed class FakeQuickActionsStore : IQuickActionsStore
    {
        private readonly List<QuickAction> _actions = new();
        public IReadOnlyList<QuickAction> LoadAll() => _actions;
        public void Save(IReadOnlyList<QuickAction> actions) { _actions.Clear(); _actions.AddRange(actions); }
        public QuickAction? FindById(string id) => _actions.FirstOrDefault(a => a.Id == id);
    }

    private sealed class FakeInstalledAppProvider : IInstalledAppProvider
    {
        public IReadOnlyList<InstalledApp> GetInstalledApps() => Array.Empty<InstalledApp>();
    }
}
