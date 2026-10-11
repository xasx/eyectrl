namespace EyeCtrl.App.Tests.Settings.Pages;

using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// Shared fixture for the T04 language-row tests: a real <see cref="SettingsWindowViewModel"/> so
/// the target-language restart prompt lands somewhere observable, recording context delegates so
/// "saved at once" and "rebuilds the page" are assertable, and a real messenger so the affirmative
/// choice's <c>RestartAppRequestedMessage</c> is observable.
/// </summary>
internal static class LanguageTestSupport
{
    internal sealed record Fixture(
        EyeCtrlSettings Settings,
        SettingsWindowViewModel ViewModel,
        WeakReferenceMessenger Messenger,
        SettingsPageContext Context,
        List<string> Rebuilds,
        List<string> Saves)
    {
        public ComboBoxSpec LanguageRow() =>
            Rows().OfType<ComboBoxSpec>().Single(c => c.Id == "g_language");

        public IReadOnlyList<SettingsRowSpec> Rows() =>
            new GeneralPageSource(Context).Build().Single().Rows;
    }

    internal static Fixture Build()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var viewModel = new SettingsWindowViewModel(
            new SettingsViewModel(settings),
            new SettingsQuickActionsEditorViewModel(
                new QaTestSupport.FakeQuickActionsStore(), new QaTestSupport.FakeInstalledAppProvider()),
            messenger, settings.Dwell);
        var rebuilds = new List<string>();
        var saves = new List<string>();
        var context = new SettingsPageContext(
            settings, viewModel, null!, null!, new CalibrationService(), messenger,
            restart: null!, supervision: null, dictionary: null,
            showModal: (_, _) => { }, showError: _ => { },
            applyAndSave: () => saves.Add("save"),
            rebuildContent: () => rebuilds.Add("rebuild"),
            invoke: action => action(), close: () => { }, readClipboard: () => "");
        return new Fixture(settings, viewModel, messenger, context, rebuilds, saves);
    }
}
