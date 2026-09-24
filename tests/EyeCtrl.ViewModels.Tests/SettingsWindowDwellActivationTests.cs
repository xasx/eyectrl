namespace EyeCtrl.ViewModels.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class SettingsWindowDwellActivationTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    // internal (not private): implements IRecipient<T>, so the MVVM IMessengerRegisterAllGenerator
    // emits code referencing it and needs at least internal visibility (same quirk as
    // DwellActivationMessagingTests.CueProbe).
    internal sealed class CueProbe : IRecipient<DwellActivationMessage>
    {
        public int Count { get; private set; }
        public bool FlagAtSend { get; private set; }
        public Func<bool>? FlagAtSendAccessor { get; set; }
        public void Receive(DwellActivationMessage message)
        {
            Count++;
            if (FlagAtSendAccessor is not null) FlagAtSend = FlagAtSendAccessor();
        }
    }

    private (SettingsWindowViewModel Vm, CueProbe Probe) Create()
    {
        var settingsVM = new SettingsViewModel(_settings);
        var quickActionsEditor = new SettingsQuickActionsEditorViewModel(
            new FakeQuickActionsStore(), new FakeInstalledAppProvider());
        var vm = new SettingsWindowViewModel(settingsVM, quickActionsEditor, _messenger, _settings.Dwell);
        var probe = new CueProbe();
        _messenger.Register(probe);
        return (vm, probe);
    }

    /// <summary>
    /// Minimal in-memory <see cref="IQuickActionsStore"/> fixture (the concrete store lives in
    /// EyeCtrl.App, which the test project does not reference). Duplicated from
    /// SettingsWindowViewModelTests, where it is private to that class.
    /// </summary>
    private sealed class FakeQuickActionsStore : IQuickActionsStore
    {
        private readonly List<QuickAction> _actions = new();

        public IReadOnlyList<QuickAction> LoadAll() => _actions;
        public void Save(IReadOnlyList<QuickAction> actions)
        {
            _actions.Clear();
            _actions.AddRange(actions);
        }
        public QuickAction? FindById(string id) => _actions.FirstOrDefault(a => a.Id == id);
    }

    /// <summary>Minimal <see cref="IInstalledAppProvider"/> fixture for the settings window VM tests.</summary>
    private sealed class FakeInstalledAppProvider : IInstalledAppProvider
    {
        public IReadOnlyList<InstalledApp> GetInstalledApps() => Array.Empty<InstalledApp>();
    }

    private static void LookAt(SettingsWindowViewModel vm)
    {
        for (var i = 1; i <= 20; i++)
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(50, 50), i * 2_000));
    }

    [Fact]
    public void ControlActivation_RunsTheRegisteredAction_ThenPublishes()
    {
        var (vm, probe) = Create();
        var actionRan = false;
        probe.FlagAtSendAccessor = () => actionRan;
        vm.Register("c1", DwellCategory.Control, 30, 25, () => actionRan = true);
        vm.UpdatePosition("c1", DwellCategory.Control, new PhysicalPoint(50, 50));

        LookAt(vm);

        Check.That(actionRan).IsTrue();
        Check.That(probe.Count).IsEqualTo(1);
        Check.That(probe.FlagAtSend).IsTrue();
    }

    [Fact]
    public void StaleControlId_NoRegisteredAction_PublishesNothing()
    {
        var (vm, probe) = Create();
        vm.Register("c1", DwellCategory.Control, 30, 25, () => { });
        // Simulate the stale shape (spec: view teardown removed the action, target left over):
        // the action dictionaries are private, so remove via reflection — the sanctioned probe
        // pattern (KeyboardPredictionExclusionTests reads private detector fields the same way).
        var actions = typeof(SettingsWindowViewModel)
            .GetField("_controlActions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(vm) as System.Collections.IDictionary;
        actions!.Remove("c1");
        vm.UpdatePosition("c1", DwellCategory.Control, new PhysicalPoint(50, 50));

        LookAt(vm);

        Check.That(probe.Count).IsEqualTo(0);
    }

    [Fact]
    public void StaleControlId_TwinWithAction_Publishes()
    {
        // Positive twin of the fact above: identical geometry and drive, action PRESENT.
        // Without this twin, a geometry mistake in the stale fact would pass silently (B2).
        var (vm, probe) = Create();
        vm.Register("c1", DwellCategory.Control, 30, 25, () => { });
        vm.UpdatePosition("c1", DwellCategory.Control, new PhysicalPoint(50, 50));

        LookAt(vm);

        Check.That(probe.Count).IsEqualTo(1);
    }

    [Fact]
    public void StaleModalId_NotAChoiceAndNoAction_PublishesNothing()
    {
        var (vm, probe) = Create();
        // A modal with one real choice; the ghost target id matches neither a choice nor an action.
        vm.ShowModal("Proceed?", [new ModalChoice("go", "Go", ModalChoiceKind.Affirmative, () => { })]);
        vm.Register("ghost", DwellCategory.Modal, 30, 25, () => { });
        var actions = typeof(SettingsWindowViewModel)
            .GetField("_modalActions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(vm) as System.Collections.IDictionary;
        actions!.Remove("ghost");
        vm.UpdatePosition("ghost", DwellCategory.Modal, new PhysicalPoint(50, 50));

        LookAt(vm); // modal visible ⇒ Receive feeds _modalDwell only

        Check.That(probe.Count).IsEqualTo(0);
    }

    [Fact]
    public void StaleModalId_TwinRealChoice_Publishes()
    {
        var (vm, probe) = Create();
        vm.ShowModal("Proceed?", [new ModalChoice("go", "Go", ModalChoiceKind.Affirmative, () => { })]);
        vm.Register("go", DwellCategory.Modal, 30, 25, () => { });
        vm.UpdatePosition("go", DwellCategory.Modal, new PhysicalPoint(50, 50));

        LookAt(vm);

        Check.That(probe.Count).IsEqualTo(1);
    }

    [Fact]
    public void SimulateActivated_MouseFallbackPath_PublishesNothing()
    {
        var (vm, probe) = Create();
        var actionRan = false;
        vm.Register("c1", DwellCategory.Control, 30, 25, () => actionRan = true);
        vm.UpdatePosition("c1", DwellCategory.Control, new PhysicalPoint(50, 50));

        vm.SimulateActivated("c1", DwellCategory.Control);

        Check.That(actionRan).IsTrue();
        Check.That(probe.Count).IsEqualTo(0);
    }

    [Fact]
    public void PublishLands_AfterTheActivationActionsSet()
    {
        // Pins the ORDER property that makes the real d_cue_sound toggle self-demonstrating:
        // the activation's Set runs before the publish, so dwelling the toggle ON cues (the
        // gate reads true by then) and dwelling it OFF is silent. (Renamed from the rev-1
        // "self-demonstrating toggle" title: this fact drives a Control action that flips
        // CueEnabled — the real row's own path is the drift guard's territory, G3.)
        var (vm, probe) = Create();
        probe.FlagAtSendAccessor = () => _settings.Dwell.CueEnabled;
        vm.Register("c1", DwellCategory.Control, 30, 25, () => _settings.Dwell.CueEnabled = true);
        vm.UpdatePosition("c1", DwellCategory.Control, new PhysicalPoint(50, 50));

        LookAt(vm);

        Check.That(probe.Count).IsEqualTo(1);
        Check.That(probe.FlagAtSend).IsTrue();
    }
}
