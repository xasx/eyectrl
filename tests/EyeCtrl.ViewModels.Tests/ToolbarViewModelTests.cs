namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

[Collection("DpiHelper Tests (ViewModels)")]
public class ToolbarViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    [Fact]
    public void InitializesWithTheNineCoreButtons()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);

        Check.That(vm.Buttons.Select(b => b.Id)).ContainsExactly(
            "Pause", "SelectionMode", "Keyboard", "Calibrate",
            "Settings", "TaskSwitcher", "Modes", "QuickActions", "ResetAdaptation");
    }

    [Fact]
    public void ToolbarViewModel_HasNoWindowActionButtons()
    {
        // Window actions moved to the Quick Actions toolbar (2026-08-09 migration). A regression
        // that re-adds them here would restore a non-removable no-confirm Close.
        var vm = new ToolbarViewModel(_settings, new WeakReferenceMessenger());
        Check.That(vm.Buttons.Select(b => b.Id))
             .Not.Contains(new[] { "Minimize", "MaximizeRestore", "Close" });
    }

    [Fact]
    public void PauseButton_SendsPauseToggledMessage()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        bool received = false;
        _messenger.Register<PauseToggledMessage>(_messenger, (_, _) => received = true);

        // Find the Pause button and simulate activation
        var pauseBtn = vm.Buttons.First(b => b.Id == "Pause");
        vm.SimulateButtonActivated("Pause");

        Check.That(received).IsTrue();
    }

    // Paused-trap guard, check 2. On 2026-09-24 a held gaze re-armed the toggle every cooldown
    // cycle (700 ms dwell + 400 ms cooldown ≈ 1.1 s), so 3.5 s of continuous gaze toggled the app
    // three times. One look must mean one toggle: after firing, the toggle cannot fire again until
    // gaze has left the target.
    [Fact]
    public void HeldGazeOnPauseToggle_TogglesExactlyOnce()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);
        var toggleCenter = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", toggleCenter);

        int toggles = 0;
        messenger.Register<PauseToggledMessage>(this, (_, _) => toggles++);

        // 3.5 s of continuous gaze on the toggle's centre.
        DwellDrive.Hold(0, 3_500_000, t => vm.Receive(new GazeCursorMovedMessage(toggleCenter, t)));

        Check.That(toggles).IsEqualTo(1);
    }

    // Paused-trap guard, check 3 (escape works). A user who starts Paused (the 09-24 launch) must
    // leave Paused by dwelling the resume target once. The message route is the production one:
    // GazeEngineAdapter registers PauseToggledMessage → InteractionManager.TogglePause.
    [Fact]
    public void ResumeDwell_LeavesPaused_ThroughTheRealStateMachine()
    {
        var messenger = new WeakReferenceMessenger();
        var interaction = new InteractionManager();
        interaction.TogglePause();          // the --paused / startPaused launch state
        Check.That(interaction.CurrentState).IsEqualTo(InteractionState.Paused);

        messenger.Register<PauseToggledMessage>(this, (_, _) => interaction.TogglePause());

        var vm = new ToolbarViewModel(_settings, messenger);
        var toggleCenter = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", toggleCenter);

        // A paused sample dwells on the toolbar (DwellActiveWhilePaused) — that opt-in IS the
        // gaze-unpause affordance.
        long dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        DwellDrive.Hold(0, dwellUs,
            t => vm.Receive(new GazeCursorMovedMessage(toggleCenter, t) { IsPaused = true }));

        Check.That(interaction.CurrentState).IsNotEqualTo(InteractionState.Paused);
    }

    // Geometry is verified at ScaleX 2.0 (the user's 3840x2400 at 200 %), not only at the
    // fixtures' default 1.0: the re-arm hit test must use the SCALED physical radius (60 DIP →
    // 120 physical px), not the raw DIP number. A 100 px drift is still on the toggle.
    [Fact]
    public void ReArmHitTest_UsesTheScaledPhysicalRadius_AtScale2()
    {
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var messenger = new WeakReferenceMessenger();
            var vm = new ToolbarViewModel(_settings, messenger);
            var toggleCenter = new PhysicalPoint(1000, 1000);
            vm.UpdateButtonScreenPosition("Pause", toggleCenter);

            int toggles = 0;
            messenger.Register<PauseToggledMessage>(this, (_, _) => toggles++);

            DwellDrive.Hold(0, 900_000, t => vm.Receive(new GazeCursorMovedMessage(toggleCenter, t)));
            Check.That(toggles).IsEqualTo(1);

            // Drift 100 physical px — inside the 120 px physical radius, so still the same look.
            var inside = new PhysicalPoint(1100, 1000);
            DwellDrive.Hold(1_000_000, 2_000_000, t => vm.Receive(new GazeCursorMovedMessage(inside, t)));
            Check.That(toggles).IsEqualTo(1);

            // Drift to 130 px — outside — and STAY there long enough to be a real leave (the
            // latch requires ~120 ms of continuous off-target). A fresh dwell then toggles again.
            var outside = new PhysicalPoint(1000, 1130);
            DwellDrive.Hold(2_100_000, 2_300_000, t => vm.Receive(new GazeCursorMovedMessage(outside, t)));
            DwellDrive.Hold(2_400_000, 3_300_000, t => vm.Receive(new GazeCursorMovedMessage(toggleCenter, t)));
            Check.That(toggles).IsEqualTo(2);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    // The other half of leave-before-re-arm: a look that leaves and returns is a second look, and
    // must be able to toggle again. A latch that never clears would strand the user in the state
    // they just entered.
    [Fact]
    public void LeavingPauseToggle_ReArmsIt_ForASecondLook()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);
        var toggleCenter = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", toggleCenter);

        int toggles = 0;
        messenger.Register<PauseToggledMessage>(this, (_, _) => toggles++);

        // First look: completes a dwell (one toggle).
        DwellDrive.Hold(0, 900_000, t => vm.Receive(new GazeCursorMovedMessage(toggleCenter, t)));
        Check.That(toggles).IsEqualTo(1);

        // Gaze leaves the target entirely — past the hit radius (60 physical px at scale 1.0).
        DwellDrive.Hold(1_000_000, 1_200_000,
            t => vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(0, 0), t)));

        // Second look: dwells again and toggles again.
        DwellDrive.Hold(1_300_000, 2_200_000, t => vm.Receive(new GazeCursorMovedMessage(toggleCenter, t)));
        Check.That(toggles).IsEqualTo(2);
    }

    // A gap-held sample carries the frozen last position, so a blink ON the toggle must not clear
    // the re-arm latch — the user's gaze never actually moved.
    [Fact]
    public void GapHeldSample_DoesNotClearTheReArmLatch()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);
        var toggleCenter = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", toggleCenter);

        int toggles = 0;
        messenger.Register<PauseToggledMessage>(this, (_, _) => toggles++);

        DwellDrive.Hold(0, 900_000, t => vm.Receive(new GazeCursorMovedMessage(toggleCenter, t)));
        Check.That(toggles).IsEqualTo(1);

        // A held point that says "eyes closed over the toggle" must not re-arm it.
        vm.Receive(new GazeCursorMovedMessage(
            new GazeFrame(new PhysicalPoint(0, 0), 1_000_000, IsInGap: true)));
        DwellDrive.Hold(1_300_000, 2_200_000, t => vm.Receive(new GazeCursorMovedMessage(toggleCenter, t)));

        Check.That(toggles).IsEqualTo(1);
    }

    // Adaptation's GeometryRouter can GROW the toolbar's hit area by up to 100 px. The latch must
    // hit-test the detector's effective geometry, not the as-registered radius: a gaze held 80 px
    // off-centre (outside the base 60 px, inside the effective 100 px) is still the same look, and
    // a latch that clears there is the 09-24 re-fire storm again — the day adaptation is enabled.
    [Fact]
    public void ReArmHitTest_UsesTheDetectorsEffectiveRadius_NotTheBaseRadius()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ProbeToolbarViewModel(_settings, messenger);
        vm.UpdateButtonScreenPosition("Pause", new PhysicalPoint(500, 500));
        vm.GrowTarget("Pause", 40); // base 60 px + 40 = 100 px effective

        int toggles = 0;
        messenger.Register<PauseToggledMessage>(this, (_, _) => toggles++);

        var offCentre = new PhysicalPoint(580, 500); // 80 px from the centre
        DwellDrive.Hold(0, 3_500_000, t => vm.Receive(new GazeCursorMovedMessage(offCentre, t)));

        Check.That(toggles).IsEqualTo(1);
    }

    // A re-dock moves the target; the latch must follow it, not hold a stale centre. The second
    // hold is on the NEW centre and must fire; the old centre must no longer be the toggle.
    [Fact]
    public void ReDockedPauseTarget_ReArmsOnTheNewCentre()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);
        var first = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", first);

        int toggles = 0;
        messenger.Register<PauseToggledMessage>(this, (_, _) => toggles++);

        DwellDrive.Hold(0, 900_000, t => vm.Receive(new GazeCursorMovedMessage(first, t)));
        Check.That(toggles).IsEqualTo(1);

        // Re-dock: the View reports a new centre for the same button.
        var moved = new PhysicalPoint(1500, 700);
        vm.UpdateButtonScreenPosition("Pause", moved);

        // Leave the old spot for good, then dwell the new centre: the toggle must fire there.
        DwellDrive.Hold(1_000_000, 1_300_000, t => vm.Receive(new GazeCursorMovedMessage(first, t)));
        DwellDrive.Hold(1_400_000, 2_300_000, t => vm.Receive(new GazeCursorMovedMessage(moved, t)));
        Check.That(toggles).IsEqualTo(2);

        // The old centre is no longer a target: a hold there must not toggle.
        DwellDrive.Hold(2_400_000, 2_600_000, t => vm.Receive(new GazeCursorMovedMessage(first, t)));
        DwellDrive.Hold(2_700_000, 3_600_000, t => vm.Receive(new GazeCursorMovedMessage(first, t)));
        Check.That(toggles).IsEqualTo(2);
    }

    // A single stray sample outside the circle is a tracker outlier, not a leave: the user is
    // still looking at the toggle, so it must not re-arm. Only a sustained off-target run does.
    [Fact]
    public void SingleOffTargetOutlier_DoesNotReArm_ButASustainedLeaveDoes()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);
        var toggleCenter = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", toggleCenter);

        int toggles = 0;
        messenger.Register<PauseToggledMessage>(this, (_, _) => toggles++);

        DwellDrive.Hold(0, 900_000, t => vm.Receive(new GazeCursorMovedMessage(toggleCenter, t)));
        Check.That(toggles).IsEqualTo(1);

        // One outlier sample off-target, then straight back: still the same look.
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(0, 0), 1_000_000));
        DwellDrive.Hold(1_030_000, 2_000_000, t => vm.Receive(new GazeCursorMovedMessage(toggleCenter, t)));
        Check.That(toggles).IsEqualTo(1);

        // A sustained leave (>= 120 ms off-target) re-arms: a new look toggles again.
        DwellDrive.Hold(2_100_000, 2_300_000,
            t => vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(0, 0), t)));
        DwellDrive.Hold(2_400_000, 3_300_000, t => vm.Receive(new GazeCursorMovedMessage(toggleCenter, t)));
        Check.That(toggles).IsEqualTo(2);
    }

    [Fact]
    public void ResetAdaptationButton_SendsResetAdaptationMessage()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        bool received = false;
        _messenger.Register<ResetAdaptationMessage>(_messenger, (_, _) => received = true);

        vm.SimulateButtonActivated("ResetAdaptation");

        Check.That(received).IsTrue();
    }

    [Fact]
    public void UpdatesPausedState_OnStateChanged()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        Check.That(vm.IsPaused).IsFalse();

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Check.That(vm.IsPaused).IsTrue();

        // Pause button icon key should change to Play
        var pauseBtn = vm.Buttons.First(b => b.Id == "Pause");
        Check.That(pauseBtn.IconKey).IsEqualTo("Play");
    }

    [Fact]
    public void SelectionModeButton_SendsSelectionModeChangedMessage()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        SelectionMode? received = null;
        _messenger.Register<SelectionModeChangedMessage>(_messenger, (_, msg) => received = msg.Mode);

        vm.SimulateButtonActivated("SelectionMode");

        Check.That(received).IsNotNull();
        Check.That(received).IsEqualTo(SelectionMode.ManualSelection);
    }

    [Fact]
    public void SelectionModeChanged_UpdatesSelectionButtonIconKey()
    {
        // No explicit RegisterAll: the constructor already auto-registers every IRecipient<T> via
        // MessengerViewModelBase's IsActive = true (ObservableRecipient). Registering again here
        // throws — WeakReferenceMessenger rejects a duplicate recipient/message-type registration.
        var vm = new ToolbarViewModel(_settings, _messenger);
        var selectionButton = vm.Buttons.First(b => b.Id == "SelectionMode");

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.None));
        Check.That(selectionButton.IconKey).IsEqualTo(SelectionModeIcons.None);

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.ManualSelection));
        Check.That(selectionButton.IconKey).IsEqualTo(SelectionModeIcons.ManualSelection);

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.AutoSelection));
        Check.That(selectionButton.IconKey).IsEqualTo(SelectionModeIcons.AutoSelection);
    }

    [Fact]
    public void SelectionModeIcons_RoundTripEveryMode()
    {
        foreach (var mode in Enum.GetValues<SelectionMode>())
            Check.WithCustomMessage($"{mode} lost its identity through the icon key")
                .That(SelectionModeIcons.ToMode(SelectionModeIcons.ForMode(mode))).IsEqualTo(mode);
    }

    [Fact]
    public void SelectionModeIcons_EmitDistinctKeysPerMode()
    {
        // With no label the icon is the button's primary state channel. The fill swap stays as a
        // secondary one (Task 5's SetSelectionModeState), but three modes sharing an icon would
        // leave colour carrying the distinction alone, which is what this design moved away from.
        var keys = Enum.GetValues<SelectionMode>().Select(SelectionModeIcons.ForMode).ToList();
        Check.That(keys.Distinct(StringComparer.Ordinal)).HasSize(keys.Count);
    }

    [Fact]
    public void SelectionModeCycle_VisitsEveryEnumValueAndWraps()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);
        var seen = new List<SelectionMode>();
        messenger.Register<SelectionModeChangedMessage>(this, (_, m) =>
        {
            seen.Add(m.Mode);
            vm.Receive(m);
        });

        int count = Enum.GetValues<SelectionMode>().Length;
        for (int i = 0; i <= count; i++)
            vm.SimulateButtonActivated("SelectionMode");

        Check.That(seen.Distinct().Count()).IsEqualTo(count);
        Check.That(seen[count]).IsEqualTo(seen[0]);
    }

    // The View (ToolbarWindow) renders five buttons — Pause, SelectionMode, Keyboard, Settings,
    // ResetAdaptation — and reports each one's real on-screen center via UpdateButtonScreenPosition.
    // The remaining three logical buttons (Calibrate, TaskSwitcher, QuickActions) are never
    // rendered and never positioned, so they must never become live gaze targets. Regression
    // guard for the "(0,0) placeholder" orphan bug: a gaze target created at the origin lets a
    // user whose gaze rests in the top-left screen corner dwell-activate an invisible button.
    [Fact]
    public void GazeAtScreenOrigin_AfterRenderedButtonsPositioned_DoesNotActivateUnrenderedButtons()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        // Simulate the View reporting real positions for the five rendered buttons only
        // (as ToolbarWindow.UpdateTargetPositions does once layout completes).
        var awayFromOrigin = new PhysicalPoint(500, 500);
        foreach (var id in new[] { "Pause", "SelectionMode", "Keyboard", "Settings", "ResetAdaptation" })
            vm.UpdateButtonScreenPosition(id, awayFromOrigin);

        bool unrenderedButtonFired = false;
        messenger.Register<CalibrationRequestedMessage>(this, (_, _) => unrenderedButtonFired = true);
        messenger.Register<TaskSwitcherToggledMessage>(this, (_, _) => unrenderedButtonFired = true);
        messenger.Register<QuickActionsToggledMessage>(this, (_, _) => unrenderedButtonFired = true);

        // Rest gaze on the screen origin for well beyond the toolbar dwell time.
        long dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        var origin = new PhysicalPoint(0, 0);
        vm.Receive(new GazeCursorMovedMessage(origin, 0));
        vm.Receive(new GazeCursorMovedMessage(origin, dwellUs));

        Check.That(unrenderedButtonFired).IsFalse();
    }

    // Positive counterpart: a button the View has positioned still activates on dwell at its
    // center, proving the lazy target-creation path produces a working gaze target.
    [Fact]
    public void GazeDwellOnPositionedButton_ActivatesIt()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", center);

        bool paused = false;
        messenger.Register<PauseToggledMessage>(this, (_, _) => paused = true);

        long dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        DwellDrive.Hold(0, dwellUs, t => vm.Receive(new GazeCursorMovedMessage(center, t)));

        Check.That(paused).IsTrue();
    }

    // Negative counterpart: the same dwell made of gap-held samples must NOT activate. A blink
    // freezes the published position on the button, and feeding that to the detector would pause
    // the app because the user's eyes happened to close over the Pause button.
    [Fact]
    public void GazeDwellOfHeldSamples_DoesNotActivateButton()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", center);

        bool paused = false;
        messenger.Register<PauseToggledMessage>(this, (_, _) => paused = true);

        long dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(new GazeFrame(center, dwellUs, IsInGap: true)));

        Check.That(paused).IsFalse();
    }

    // Target-lifecycle contract (Task 7): a hidden/calibrating toolbar must not dwell — Close is
    // no-confirm, so a gaze pass over where Close *used to be* must not fire it on the current
    // foreground window. SetActive(false) gates Receive AND clears in-progress dwell state.
    [Fact]
    public void SetActive_False_HiddenToolbarDoesNotDwell()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", center);
        vm.SetActive(false);  // hidden

        bool fired = false;
        messenger.Register<PauseToggledMessage>(this, (_, _) => fired = true);

        var dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, dwellUs));

        Check.That(fired).IsFalse();
    }

    // No instant-activation on resume — ResetAllState cleared the stale EnteredTimestamp on hide,
    // so a single sample immediately after reactivation cannot complete a dwell.
    [Fact]
    public void SetActive_True_AfterHide_DoesNotInstantlyActivate()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", center);

        // Partially dwell, then hide mid-dwell.
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, 100_000)); // partial (100ms < 700ms dwell)
        vm.SetActive(false);
        vm.SetActive(true);

        bool fired = false;
        messenger.Register<PauseToggledMessage>(this, (_, _) => fired = true);

        // A single sample immediately after resume must NOT complete a dwell — the stale
        // EnteredTimestamp was cleared by ResetAllState on hide.
        vm.Receive(new GazeCursorMovedMessage(center, 200_000));
        Check.That(fired).IsFalse();
    }

    [Fact]
    public void TargetProgress_RaisesAndClearsIsHighlighted()
    {
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var button = vm.Buttons.Single(b => b.Id == "Pause");

        Check.That(button.IsHighlighted).IsFalse();
        vm.SimulateTargetProgress("Pause", 0.02);
        Check.That(button.IsHighlighted).IsTrue();
        vm.SimulateTargetProgress("Pause", 0.0);
        Check.That(button.IsHighlighted).IsFalse();
    }

    [Fact]
    public void TargetActivated_ClearsIsHighlightedWithTheProgress()
    {
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var button = vm.Buttons.Single(b => b.Id == "Pause");

        vm.SimulateTargetProgress("Pause", 0.9);
        vm.SimulateButtonActivated("Pause");

        Check.That(button.DwellProgress).IsEqualTo(0.0);
        Check.That(button.IsHighlighted).IsFalse();
    }

    // Task 5's IsHighlighted derivation ("progress > 0") rests on a specific DwellDetector
    // contract: no progress on the first sample inside a target, a positive value from the second
    // sample on, and exactly 0 on leave. The two tests above drive that contract through
    // SimulateTargetProgress, which bypasses the real detector entirely and so cannot catch a
    // regression in it — e.g. DwellDetector no longer firing 0 on leave (DwellDetector.cs:253).
    // This one drives the same path production gaze does.
    [Fact]
    public void RealGaze_EnteringThenLeaving_RaisesThenClearsIsHighlighted()
    {
        var vm = new ToolbarViewModel(_settings, new WeakReferenceMessenger());
        var button = vm.Buttons.Single(b => b.Id == "Pause");

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", center);

        // First sample inside the target: DwellDetector only marks entry, no progress event yet.
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        Check.That(button.IsHighlighted).IsFalse();

        // Second sample, one gaze interval later: progress fires positive for the first time.
        vm.Receive(new GazeCursorMovedMessage(center, 30_000));
        Check.That(button.IsHighlighted).IsTrue();

        // Gaze leaves the target entirely: DwellDetector fires exactly 0.
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(0, 0), 60_000));
        Check.That(button.IsHighlighted).IsFalse();
    }

    [Fact]
    public void ModesButton_ShowsTheLockedActionGlyph_AndResetsOnModeExit()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        var modes = vm.Buttons.Single(b => b.Id == "Modes");
        Check.That(modes.IconKey).IsEqualTo("Modes");

        _messenger.Send(new RepeatActionChangedMessage(InteractionAction.RightClick));
        Check.That(modes.IconKey).IsEqualTo("ModeRightClick");

        // Leaving the mode via the uniform exit path (StateChanged off) also resets.
        _messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(modes.IconKey).IsEqualTo("Modes");
    }
}

/// <summary>
/// Exposes the detector so a test can apply the adaptation geometry margin, which production
/// applies through <c>IDwellDetectorRegistry</c>/<c>GeometryRouter</c>. Top-level (not nested in
/// the test class): the MVVM Toolkit's RegisterAll source generator emits code for any class
/// implementing <see cref="CommunityToolkit.Mvvm.Messaging.IRecipient{T}"/>, and a private nested
/// subclass is not visible to that generated code.
/// </summary>
internal sealed class ProbeToolbarViewModel : ToolbarViewModel
{
    public ProbeToolbarViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(settings, messenger) { }

    public void GrowTarget(string id, double marginPx) => DwellDetector.UpdateTargetGeometry(id, marginPx);
}