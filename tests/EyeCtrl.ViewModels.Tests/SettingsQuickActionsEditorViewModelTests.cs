namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

public class SettingsQuickActionsEditorViewModelTests
{
    private sealed class FakeStore : IQuickActionsStore
    {
        public List<QuickAction> Saved { get; private set; } = new();
        public IReadOnlyList<QuickAction> LoadAll() => Saved;
        public void Save(IReadOnlyList<QuickAction> actions) => Saved = actions.ToList();
        public QuickAction? FindById(string id) => Saved.FirstOrDefault(a => a.Id == id);
    }

    private sealed class FakeApps : IInstalledAppProvider
    {
        private readonly IReadOnlyList<InstalledApp> _apps;
        public FakeApps(params InstalledApp[] apps) => _apps = apps;
        public IReadOnlyList<InstalledApp> GetInstalledApps() => _apps;
    }

    private static SettingsQuickActionsEditorViewModel Vm(FakeStore store, IInstalledAppProvider? apps = null)
        => new(store, apps ?? new FakeApps());

    [Fact]
    public void AddKeyboardShortcut_CreatesAndPersistsAction()
    {
        var store = new FakeStore();
        var vm = Vm(store);
        vm.AddKeyboardShortcut("Copy", "Ctrl+C");

        Check.That(vm.Actions).HasSize(1);
        Check.That(store.Saved).HasSize(1);
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.KeyboardShortcut);
        Check.That(store.Saved[0].Label).IsEqualTo("Copy");
        Check.That(store.Saved[0].Payload).IsEqualTo("Ctrl+C");
        Check.That(store.Saved[0].Enabled).IsTrue();
    }

    [Fact]
    public void AddMediaControl_CreatesMediaAction()
    {
        var store = new FakeStore();
        Vm(store).AddMediaControl("Play / Pause", "PlayPause");
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.MediaControl);
        Check.That(store.Saved[0].Payload).IsEqualTo("PlayPause");
    }

    [Fact]
    public void AddWindowAction_CreatesAndPersistsWindowAction()
    {
        var store = new FakeStore();
        Vm(store).AddWindowAction("Close Window", "Close");

        // Add() persists immediately, like every other add-path — asserting through store.Saved
        // (not the VM collection) proves the write reached the store.
        Check.That(store.Saved).HasSize(1);
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.WindowAction);
        Check.That(store.Saved[0].Label).IsEqualTo("Close Window");
        Check.That(store.Saved[0].Payload).IsEqualTo("Close");
        Check.That(store.Saved[0].Enabled).IsTrue();
    }

    [Fact]
    public void AddApp_CreatesLaunchWindowsAppWithAumidAndName()
    {
        var store = new FakeStore();
        Vm(store).AddApp(new InstalledApp("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.LaunchWindowsApp);
        Check.That(store.Saved[0].Label).IsEqualTo("Calculator");
        Check.That(store.Saved[0].Payload).IsEqualTo("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
    }

    [Fact]
    public void ToggleEnabled_And_Delete_PersistById()
    {
        var store = new FakeStore();
        var vm = Vm(store);
        vm.AddMediaControl("Mute", "VolumeMute");
        var id = vm.Actions[0].Id;

        vm.ToggleEnabled(id);
        Check.That(store.Saved[0].Enabled).IsFalse();

        vm.DeleteAction(id);
        Check.That(vm.Actions).IsEmpty();
        Check.That(store.Saved).IsEmpty();
    }

    [Fact]
    public void Load_PopulatesFromStore_IncludingDisabled()
    {
        var store = new FakeStore();
        store.Save(new[]
        {
            new QuickAction { Type = QuickActionType.CannedText, Label = "Hi", Payload = "hi", Enabled = false },
        });
        var vm = Vm(store);
        vm.Load();
        Check.That(vm.Actions).HasSize(1);
        Check.That(vm.Actions[0].Enabled).IsFalse();
    }

    [Fact]
    public void InstalledApps_ComesFromProvider()
    {
        var apps = new FakeApps(new InstalledApp("A", "a!x"), new InstalledApp("B", "b!x"));
        Check.That(Vm(new FakeStore(), apps).InstalledApps).HasSize(2);
    }
}
