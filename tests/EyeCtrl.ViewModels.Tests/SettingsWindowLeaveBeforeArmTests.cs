namespace EyeCtrl.ViewModels.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The Settings window's leave-before-arm latch (A2 fixup-01 H1/M3): a modal choice shown under a
/// resting gaze, and a window target the gaze rests on when a modal closes, stay disarmed until the
/// gaze has been outside them. Driven through the real <see cref="SettingsWindowViewModel"/> gaze
/// path (<c>Receive</c>), the targets registered and positioned the way the view does.
/// </summary>
public class SettingsWindowLeaveBeforeArmTests
{
    private const double Radius = 70;
    private const int DwellMs = 300;
    private const long FrameUs = 16_000;

    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();
    private long _now = 1_000_000;

    private static readonly PhysicalPoint Button = new(1000, 500);
    private static readonly PhysicalPoint Away = new(1600, 900);

    [Fact]
    public void AModalChoiceShownUnderTheGaze_DoesNotDwell_WhileTheGazeRests()
    {
        var vm = Create();
        var chosen = 0;
        ShowOneChoice(vm, () => chosen++);

        Gaze(vm, Button, ms: 3000);

        Check.That(chosen).IsEqualTo(0);
        Check.That(vm.IsModalVisible).IsTrue();
    }

    [Fact]
    public void AModalChoiceShownUnderTheGaze_ArmsOnceTheGazeHasLeft()
    {
        var vm = Create();
        var chosen = 0;
        ShowOneChoice(vm, () => chosen++);
        Gaze(vm, Button, ms: 500);

        Gaze(vm, Away, ms: 100);
        Gaze(vm, Button, ms: 2000);

        Check.That(chosen).IsEqualTo(1);
    }

    [Fact]
    public void AModalChoiceShownAwayFromTheGaze_DwellsAsBefore()
    {
        var vm = Create();
        var chosen = 0;
        ShowOneChoice(vm, () => chosen++);

        Gaze(vm, Away, ms: 100);
        Gaze(vm, Button, ms: 2000);

        Check.That(chosen).IsEqualTo(1);
    }

    [Fact]
    public void AnUnpositionedChoice_StaysHeld_SoThePlaceholderOriginCannotArmIt()
    {
        // Registered at the detector's placeholder (0,0) and not positioned yet: the gaze is far
        // from the origin, but that says nothing about where the button will be drawn.
        var vm = Create();
        var chosen = 0;
        vm.ShowModal("q", [new ModalChoice("only", "OK", ModalChoiceKind.Affirmative, () => chosen++)]);
        vm.Register("only", DwellCategory.Modal, Radius, DwellMs, () => { });

        Gaze(vm, Button, ms: 200);                                       // not positioned: held
        vm.UpdatePosition("only", DwellCategory.Modal, Button);           // now drawn under the gaze
        Gaze(vm, Button, ms: 3000);

        Check.That(chosen).IsEqualTo(0);
    }

    [Fact]
    public void AControlTheGazeRestsOnWhenTheModalCloses_IsHeldUntilTheGazeLeaves()
    {
        var vm = Create();
        var control = 0;
        vm.Register("row", DwellCategory.Control, Radius, DwellMs, () => control++);
        vm.UpdatePosition("row", DwellCategory.Control, Button);

        ShowOneChoice(vm, () => { });
        Gaze(vm, Away, ms: 100);
        Gaze(vm, Button, ms: 2000);                                      // answers the modal …
        Check.That(vm.IsModalVisible).IsFalse();

        Gaze(vm, Button, ms: 3000);                                      // … and keeps resting
        Check.That(control).IsEqualTo(0);

        Gaze(vm, Away, ms: 100);
        Gaze(vm, Button, ms: 2000);
        Check.That(control).IsStrictlyGreaterThan(0);   // armed again: a real look dwells it
    }

    [Fact]
    public void AControlRegisteredByTheClosingChoice_IsHeldToo()
    {
        // A mode switch rebuilds the page from inside the choice: the rows it registers sit under
        // the resting gaze from their first frame.
        var vm = Create();
        var control = 0;
        vm.ShowModal("q", [new ModalChoice("only", "OK", ModalChoiceKind.Affirmative, () =>
        {
            vm.ClearCategory(DwellCategory.Control);
            vm.Register("rebuilt", DwellCategory.Control, Radius, DwellMs, () => control++);
            vm.UpdatePosition("rebuilt", DwellCategory.Control, Button);
        })]);
        RegisterShown(vm);
        Gaze(vm, Away, ms: 100);
        Gaze(vm, Button, ms: 2000);
        Check.That(vm.IsModalVisible).IsFalse();

        Gaze(vm, Button, ms: 3000);

        Check.That(control).IsEqualTo(0);
    }

    [Fact]
    public void WithNoModalEver_AControlUnderTheGaze_DwellsAtOnce_AsBefore()
    {
        var vm = Create();
        var control = 0;
        vm.Register("row", DwellCategory.Control, Radius, DwellMs, () => control++);
        vm.UpdatePosition("row", DwellCategory.Control, Button);

        Gaze(vm, Button, ms: 2000);

        Check.That(control).IsGreaterOrEqualThan(1);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private SettingsWindowViewModel Create()
    {
        var settingsVM = new SettingsViewModel(_settings);
        var editor = new SettingsQuickActionsEditorViewModel(new FakeQuickActionsStore(), new FakeInstalledAppProvider());
        return new SettingsWindowViewModel(settingsVM, editor, _messenger, _settings.Dwell);
    }

    private void ShowOneChoice(SettingsWindowViewModel vm, Action onChosen)
    {
        vm.ShowModal("q", [new ModalChoice("only", "OK", ModalChoiceKind.Affirmative, onChosen)]);
        RegisterShown(vm);
    }

    /// <summary>What <c>UpdateModalVisibility</c> + the per-frame position push do for the card on screen.</summary>
    private static void RegisterShown(SettingsWindowViewModel vm)
    {
        foreach (var c in vm.ModalChoices)
        {
            vm.Register(c.Id, DwellCategory.Modal, Radius, DwellMs, () => { });
            vm.UpdatePosition(c.Id, DwellCategory.Modal, Button);
        }
    }

    private void Gaze(SettingsWindowViewModel vm, PhysicalPoint at, int ms)
    {
        for (long elapsed = 0; elapsed < ms * 1000L; elapsed += FrameUs)
        {
            _now += FrameUs;
            vm.Receive(new GazeCursorMovedMessage(at, _now));
        }
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
