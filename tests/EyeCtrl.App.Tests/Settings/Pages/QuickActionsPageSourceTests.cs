namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using CommunityToolkit.Mvvm.Messaging;
using NFluent;
using Xunit;

public class QuickActionsPageSourceTests
{
    private static SettingsPageContext Ctx() => TestSettingsPageContext.Build(viewModel: MakeQaViewModel());

    // Mirrors SettingsWindowViewModelTests.CreateViewModel() (ViewModel.Tests:23-29). The two fakes
    // are private nested there, so they are re-declared here — they are 3-line stubs.
    private static SettingsWindowViewModel MakeQaViewModel()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var settingsService = new SettingsService("test_qa_source_" + System.Guid.NewGuid() + ".json");
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

    [Fact]
    public void ListPage_HasConfigControlsAndAddButton()
    {
        var rows = new QuickActionsPageSource(Ctx()).Build().First().Rows;
        Check.That(rows.OfType<ToggleSpec>().First().Id).IsEqualTo("qa_enabled");
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id)).Contains("qa_add");
    }

    [Fact]
    public void ShortcutPicker_ButtonsUsePickerButtonWidth()
    {
        var src = new QuickActionsPageSource(Ctx());
        src.GoTo(QuickActionsPageSource.QaView.PickShortcut);   // direct call — GoTo/QaView are internal

        var preset = src.Build().First().Rows.OfType<ButtonSpec>().First(b => !b.Id.StartsWith("qa_back"));
        Check.That(preset.Width).IsEqualTo(SettingsWindow.PickerButtonWidth);
    }

    [Fact]
    public void ShortcutPicker_BackButtonHasPerPageId()
    {
        var src = new QuickActionsPageSource(Ctx());
        src.GoTo(QuickActionsPageSource.QaView.PickShortcut);
        var back = src.Build().First().Rows.OfType<ButtonSpec>().First(b => b.Id.StartsWith("qa_back"));
        Check.That(back.Id).IsEqualTo("qa_back_0");   // R5: per-page id preserved, not a constant "qa_back"
    }
}
