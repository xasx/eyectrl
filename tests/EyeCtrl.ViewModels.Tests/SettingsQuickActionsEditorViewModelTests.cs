namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

public class SettingsQuickActionsEditorViewModelTests
{
    private sealed class FakeStore : IQuickActionsStore
    {
        public List<QuickAction> Saved { get; private set; } = new();

        /// <summary>How many times the VM wrote — the one-time match must write at most once.</summary>
        public int SaveCount { get; private set; }

        /// <summary>Seeds storage without counting as a VM write, for load-path tests.</summary>
        public void Seed(params QuickAction[] actions) => Saved = actions.ToList();

        public IReadOnlyList<QuickAction> LoadAll() => Saved;
        public void Save(IReadOnlyList<QuickAction> actions) { Saved = actions.ToList(); SaveCount++; }
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
        vm.AddKeyboardShortcut("Copy", "Ctrl+C", "kbd.copy");

        Check.That(vm.Actions).HasSize(1);
        Check.That(store.Saved).HasSize(1);
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.KeyboardShortcut);
        Check.That(store.Saved[0].Label).IsEqualTo("Copy");
        Check.That(store.Saved[0].Payload).IsEqualTo("Ctrl+C");
        Check.That(store.Saved[0].Enabled).IsTrue();
        // Adding a preset stores its stable id beside the readable English label (D2).
        Check.That(store.Saved[0].PresetId).IsEqualTo("kbd.copy");
    }

    [Fact]
    public void AddMediaControl_CreatesMediaAction()
    {
        var store = new FakeStore();
        Vm(store).AddMediaControl("Play / Pause", "PlayPause", "media.play_pause");
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.MediaControl);
        Check.That(store.Saved[0].Payload).IsEqualTo("PlayPause");
        Check.That(store.Saved[0].PresetId).IsEqualTo("media.play_pause");
    }

    [Fact]
    public void AddWindowAction_CreatesAndPersistsWindowAction()
    {
        var store = new FakeStore();
        Vm(store).AddWindowAction("Close Window", "Close", "window.close");

        // Add() persists immediately, like every other add-path — asserting through store.Saved
        // (not the VM collection) proves the write reached the store.
        Check.That(store.Saved).HasSize(1);
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.WindowAction);
        Check.That(store.Saved[0].Label).IsEqualTo("Close Window");
        Check.That(store.Saved[0].Payload).IsEqualTo("Close");
        Check.That(store.Saved[0].Enabled).IsTrue();
        Check.That(store.Saved[0].PresetId).IsEqualTo("window.close");
    }

    [Fact]
    public void AddApp_CreatesLaunchWindowsAppWithAumidAndName()
    {
        var store = new FakeStore();
        Vm(store).AddApp(new InstalledApp("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
        Check.That(store.Saved[0].Type).IsEqualTo(QuickActionType.LaunchWindowsApp);
        Check.That(store.Saved[0].Label).IsEqualTo("Calculator");
        Check.That(store.Saved[0].Payload).IsEqualTo("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        // An app pick has no preset: the tile is named by the installed app.
        Check.That(store.Saved[0].PresetId).IsNull();
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
    public void Load_MatchesExistingTilesOnce_AndWritesExactlyOnce()
    {
        var matching = new QuickAction { Type = QuickActionType.KeyboardShortcut, Label = "Copy", Payload = "Ctrl+C" };
        var renamed = new QuickAction { Type = QuickActionType.KeyboardShortcut, Label = "Mein Kopieren", Payload = "Ctrl+C" };
        var canned = new QuickAction { Type = QuickActionType.CannedText, Label = "Hi", Payload = "hi" };
        var store = new FakeStore();
        store.Seed(matching, renamed, canned);
        var vm = Vm(store);

        vm.Load();

        // The exact match gets its id; the user-named and drifted tiles are untouched; nothing
        // is dropped, and the match wrote the list back once.
        Check.That(matching.PresetId).IsEqualTo("kbd.copy");
        Check.That(renamed.PresetId).IsNull();
        Check.That(canned.PresetId).IsNull();
        Check.That(vm.Actions).HasSize(3);
        Check.That(store.SaveCount).IsEqualTo(1);
        Check.That(store.Saved).HasSize(3);

        // A second load has nothing left to fill: no second write.
        vm.Load();
        Check.That(store.SaveCount).IsEqualTo(1);
        Check.That(vm.Actions).HasSize(3);
    }

    [Fact]
    public void Load_NoMatchingPreset_WritesNothing()
    {
        var store = new FakeStore();
        store.Seed(new QuickAction { Type = QuickActionType.CannedText, Label = "Hi", Payload = "hi" });
        var vm = Vm(store);

        vm.Load();

        Check.That(vm.Actions).HasSize(1);
        Check.That(store.SaveCount).IsEqualTo(0);
    }

    [Fact]
    public void InstalledApps_ComesFromProvider()
    {
        var apps = new FakeApps(new InstalledApp("A", "a!x"), new InstalledApp("B", "b!x"));
        Check.That(Vm(new FakeStore(), apps).InstalledApps).HasSize(2);
    }
}
