namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// Shared fixture for the two Quick Actions page-source test classes
/// (<see cref="QuickActionsPageSourceTests"/> and <see cref="SettingsPageSourceCallbackTests"/>):
/// builds a real <see cref="SettingsWindowViewModel"/> over an in-memory store and an empty
/// installed-app provider. Mirrors <c>SettingsWindowViewModelTests.CreateViewModel()</c>; the two
/// fakes are re-declared here (they are private nested in the ViewModel test) so there is ONE copy,
/// not one per test class.
/// </summary>
internal static class QaTestSupport
{
    public static SettingsWindowViewModel MakeViewModel()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var settingsService = new SettingsService("test_qa_" + System.Guid.NewGuid() + ".json");
        var settingsVM = new SettingsViewModel(settings, settingsService, messenger);
        var editor = new SettingsQuickActionsEditorViewModel(
            new FakeQuickActionsStore(), new FakeInstalledAppProvider());
        var vm = new SettingsWindowViewModel(settingsVM, editor, messenger, settings.Dwell);
        editor.Load();   // production calls Load() right after constructing the editor
        return vm;
    }

    internal sealed class FakeQuickActionsStore : IQuickActionsStore
    {
        private readonly List<QuickAction> _actions = new();
        public IReadOnlyList<QuickAction> LoadAll() => _actions;
        public void Save(IReadOnlyList<QuickAction> actions) { _actions.Clear(); _actions.AddRange(actions); }
        public QuickAction? FindById(string id) => _actions.FirstOrDefault(a => a.Id == id);
    }

    internal sealed class FakeInstalledAppProvider : IInstalledAppProvider
    {
        public IReadOnlyList<InstalledApp> GetInstalledApps() => Array.Empty<InstalledApp>();
    }
}
