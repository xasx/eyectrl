namespace EyeCtrl.App.Tests.Services;

using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// T2 — the note gate (P2 D1–D4, D6, D7 chrome zones). One test per gate row, the pause freeze,
/// the occluders, the own-window conjunct over two overlapping notes, the dwell floor and the
/// geometry opt-out through the real router, and the chrome rects. Geometry and dwell run at the
/// user's DPI 2.0 (the same discipline <see cref="NoteManagerTests"/> pins); every centre is
/// computed through the production math, never hand-derived logical numbers.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteGateTests
{
    // ── gate row 2: pause ──

    [Fact]
    public void PausedSamples_NeverFire_AndEditStaysInPlace()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = manager.AddNoteTargetsForTest(anchor);
            var closeCenter = ButtonCentrePhysical(anchor, 2);

            // A full 2 s hold on ✕, every sample carrying IsPaused: nothing may fire...
            for (long t = 0; t <= 2_000_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(
                    new GazeFrame(closeCenter, t, IsInGap: false)) { IsPaused = true });
            Check.WithCustomMessage("paused samples must not complete a destructive close dwell")
                .That(manager.ActiveNotes).HasSize(1);

            // ...and the same freeze holds the editor: a paused hold on Done must not exit Edit.
            var editId = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(4000, 1000));
            manager.Apply(new NoteAppend("draft"));
            var doneCenter = ButtonCentrePhysical(new PhysicalPoint(4000, 1000), 0);
            for (long t = 2_100_000; t <= 3_000_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(
                    new GazeFrame(doneCenter, t, IsInGap: false)) { IsPaused = true });
            Check.WithCustomMessage("pause freezes Edit in place — Done must not fire")
                .That(manager.IsEditing).IsTrue();
            Check.That(manager.ActiveNotes.Any(n => n.Id == editId)).IsTrue();
            Check.That(id).IsNotEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Fact]
    public void ThePausePoll_CatchesAPause_TheSampleFlagMissed()
    {
        // P2 D1 row 2: the poll reads InteractionManager.CurrentState per sample, so a pause the
        // sample's flag does not carry (entered from the tray) still freezes the notes.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);
        var im = new InteractionManager();
        manager.SetPausePoll(() => im.CurrentState == InteractionState.Paused);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);
            var closeCenter = ButtonCentrePhysical(anchor, 2);

            im.TogglePause(); // the only pause writer (P2 D1): CurrentState == Paused
            for (long t = 0; t <= 1_500_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.WithCustomMessage("a polled pause must freeze the notes even with IsPaused=false samples")
                .That(manager.ActiveNotes).HasSize(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Fact]
    public void PauseResume_NeedsALeave_BeforeATargetUnderTheGazeFires()
    {
        // P2 D1 row 2's recovery: reset + arm-latch. The gaze sat on ✕ while paused; on resume the
        // dwell starts fresh AND ✕ is latched, so it fires only after a real leave.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);
            var closeCenter = ButtonCentrePhysical(anchor, 2);

            // Paused, held on ✕.
            for (long t = 0; t <= 1_000_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(
                    new GazeFrame(closeCenter, t, IsInGap: false)) { IsPaused = true });
            Check.That(manager.ActiveNotes).HasSize(1);

            // Resume, still on ✕: the recovery latches it — a further second must not fire.
            for (long t = 1_100_000; t <= 2_100_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.WithCustomMessage("a target that sat under the paused gaze must be latched on resume")
                .That(manager.ActiveNotes).HasSize(1);

            // Leave past the 150 ms release, return, and the ✕ works again — into the chip (P2 D5).
            for (long t = 2_200_000; t <= 2_400_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(
                    new GazeFrame(new PhysicalPoint(closeCenter.X + 10_000, closeCenter.Y), t, IsInGap: false)));
            for (long t = 2_500_000; t <= 3_300_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── gate row 3: the suspend flags the manager keeps from messages ──

    [Fact]
    public void EyeCareExercise_SuspendsNotes_UntilTheMessageClears()
    {
        // P2 D1: the exercise window is click-through and full-screen — WindowFromPoint sees the
        // note under it, so only the flag stops a hidden ✕ from firing.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);
            var closeCenter = ButtonCentrePhysical(anchor, 2);

            messenger.Send(new EyeCareExerciseActiveMessage(true));
            for (long t = 0; t <= 2_000_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.WithCustomMessage("a note under a running exercise must never fire")
                .That(manager.ActiveNotes).HasSize(1);

            // The (false) arrives after the summary; the recovery latches ✕ under the held gaze —
            // so the first clear second must not fire either.
            messenger.Send(new EyeCareExerciseActiveMessage(false));
            for (long t = 2_100_000; t <= 3_100_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.WithCustomMessage("the post-exercise recovery must arm-latch the target under the gaze")
                .That(manager.ActiveNotes).HasSize(1);

            for (long t = 3_200_000; t <= 3_400_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(
                    new GazeFrame(new PhysicalPoint(closeCenter.X + 10_000, closeCenter.Y), t, IsInGap: false)));
            for (long t = 3_500_000; t <= 4_300_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Theory]
    [InlineData(CalibrationState.Collecting)]
    [InlineData(CalibrationState.Training)]
    public void CalibrationCollectingOrTraining_SuspendsNotes(CalibrationState state)
    {
        // P2 D1 (review-03 B-2): the calibration window turns click-through at runtime when
        // Collecting starts, and the owner fixates screen edges and corners where notes sit.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);
            var closeCenter = ButtonCentrePhysical(anchor, 2);

            messenger.Send(new CalibrationStateChangedMessage(state, 0));
            for (long t = 0; t <= 2_000_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.WithCustomMessage($"a note under calibration {state} must never fire")
                .That(manager.ActiveNotes).HasSize(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Fact]
    public void CalibrationEnd_Resumes_AndArmLatches()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);
            var closeCenter = ButtonCentrePhysical(anchor, 2);

            messenger.Send(new CalibrationStateChangedMessage(CalibrationState.Collecting, 0));
            for (long t = 0; t <= 1_000_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));

            messenger.Send(new CalibrationStateChangedMessage(CalibrationState.Active, 100));
            for (long t = 1_100_000; t <= 2_100_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.WithCustomMessage("the calibration-end recovery must arm-latch the held target")
                .That(manager.ActiveNotes).HasSize(1);

            for (long t = 2_200_000; t <= 2_400_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(
                    new GazeFrame(new PhysicalPoint(closeCenter.X + 10_000, closeCenter.Y), t, IsInGap: false)));
            for (long t = 2_500_000; t <= 3_300_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Theory]
    [InlineData(InteractionState.FreeLook, false)]
    [InlineData(InteractionState.Fixation, false)]
    [InlineData(InteractionState.Paused, false)] // pause is gate row 2, not a suspender
    [InlineData(InteractionState.ActionMenuVisible, true)]
    [InlineData(InteractionState.ManualSelectionActive, true)]
    [InlineData(InteractionState.ManualSelectionActionMenuVisible, true)]
    [InlineData(InteractionState.ScrollMode, true)]
    [InlineData(InteractionState.Dragging, true)]
    [InlineData(InteractionState.RepeatMode, true)]
    public void TheSuspendingInteractionStates_AreExactlyThePlanSet(InteractionState state, bool suspends)
    {
        // P2 D1: the interaction-mode suspender set (review 02 confirmed it complete). The keyboard
        // is deliberately absent — Done must stay live while typing.
        Check.That(NoteManager.InteractionStateSuspendsNotes(state)).IsEqualTo(suspends);
    }

    // ── gate row 4: the occluders (P2 D3) ──

    [Fact]
    public void DriftNoticeOverClose_NeverFiresTheNote()
    {
        OccluderOverClose_NeverFires(
            (manager, rect) => manager.SetOccluderSources(
                driftNoticeBounds: () => rect()),
            "the drift notice's banner must occlude ✕ beneath it");
    }

    [Fact]
    public void CaregiverAlertOverClose_NeverFiresTheNote()
    {
        OccluderOverClose_NeverFires(
            (manager, rect) => manager.SetOccluderSources(
                caregiverAlertBounds: () => rect()),
            "the caregiver alert's banner must occlude ✕ beneath it");
    }

    [Fact]
    public void LagMarkOverClose_NeverFiresTheNote()
    {
        OccluderOverClose_NeverFires(
            (manager, rect) => manager.SetOccluderSources(
                lagMarkRects: () => rect() is { } r ? new[] { r } : Array.Empty<PhysicalRect>()),
            "the lag mark's hit area must occlude ✕ beneath it");
    }

    [Fact]
    public void TrackStatusCardOverClose_NeverFiresTheNote()
    {
        // Fixup 01 M1: the track-status card is opaque, click-through and parked top-right for as
        // long as TrackStatusSettings.Enabled — WindowFromPoint reports the note beneath it, so
        // its visible bounds must occlude.
        OccluderOverClose_NeverFires(
            (manager, rect) => manager.SetOccluderSources(trackStatusBounds: rect),
            "the track-status card must occlude ✕ beneath it");
    }

    private static void OccluderOverClose_NeverFires(
        Action<NoteManager, Func<PhysicalRect?>> wire, string because)
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);
        PhysicalRect? occluding = null;
        wire(manager, () => occluding);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);
            var closeCenter = ButtonCentrePhysical(anchor, 2);

            // The notice sits exactly over ✕: a full hold must not fire.
            occluding = new PhysicalRect(closeCenter.X - 300, closeCenter.Y - 100, 600, 200);
            for (long t = 0; t <= 1_500_000; t += 100_000)
                manager.OnGazeMoved(closeCenter, t);
            Check.WithCustomMessage(because).That(manager.ActiveNotes).HasSize(1);

            // The notice is gone: the same held gaze now dwells ✕ — the hold above was the occluder.
            // P2 D5: the completed ✕ enters the chip, it does not delete.
            occluding = null;
            for (long t = 1_600_000; t <= 2_400_000; t += 100_000)
                manager.OnGazeMoved(closeCenter, t);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── gate row 4: the own-window conjunct over two overlapping notes (P2 D2) ──

    [StaFact]
    public void OfTwoOverlappingNotes_TheTopOneFires_TheBottomNeverDoes()
    {
        // The fake hit tester decides which window the gaze is over. Two notes 40 px apart, so
        // both ✕ circles hit the same gaze point and the BOTTOM one is strictly nearest: without
        // the conjunct the bottom ✕ (nearest, distance 0) fires — this test goes red. With it,
        // the bottom target is filtered and the top one — 40 px off, also hitting — takes over.
        var messenger = new WeakReferenceMessenger();
        var tester = new FakeHitTester();
        using var manager = new NoteManager(messenger, sink: null, dwellRegistry: null, hitTester: tester);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchorBottom = new PhysicalPoint(1600, 1000);
            var anchorTop = new PhysicalPoint(1640, 1000);

            var bottomId = TypeIntoNewSavedNote(manager, "bottom", anchorBottom);
            var topId = TypeIntoNewSavedNote(manager, "top", anchorTop);
            var bottomWindow = manager.WindowForTest(bottomId);
            var topWindow = manager.WindowForTest(topId);
            Check.That(bottomWindow).IsNotNull();
            Check.That(topWindow).IsNotNull();
            var bottomHandle = new WindowInteropHelper(bottomWindow!).EnsureHandle();
            var topHandle = new WindowInteropHelper(topWindow!).EnsureHandle();

            var bottomClose = ButtonCentrePhysical(anchorBottom, 2);

            // The gaze is over the BOTTOM note's ✕, but the TOP window is hit-testably on top.
            tester.Answer = topHandle;

            ArmThenHold(manager, bottomClose, startUs: 0);
            Check.WithCustomMessage("the bottom note's ✕ must never fire under a foreign window")
                .That(manager.ActiveNotes.First(n => n.Id == bottomId).State).IsEqualTo(NoteState.Display);
            Check.WithCustomMessage("the top note — whose window is over the gaze — takes the dwell")
                .That(manager.ActiveNotes.First(n => n.Id == topId).State).IsEqualTo(NoteState.PendingUndo);

            // Now the bottom window is genuinely on top: the same spot fires its ✕ — into the chip.
            tester.Answer = bottomHandle;
            ArmThenHold(manager, bottomClose, startUs: 2_000_000);
            Check.WithCustomMessage("with its own window under the gaze, the bottom ✕ must fire")
                .That(manager.ActiveNotes.First(n => n.Id == bottomId).State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheOwnWindowConjunct_IsInactive_ForTheSentinelTester()
    {
        // Fixup 01 M2: the constructor defaults to the production tester, so the inactive case is
        // the explicit InactiveNoteHitTester sentinel — and with it the plain dwell behaves
        // exactly as if the conjunct did not exist (headless determinism).
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);
        Check.That(manager.HitTesterForTest).IsInstanceOf<InactiveNoteHitTester>();

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            TypeIntoNewSavedNote(manager, "x", anchor);
            var closeCenter = ButtonCentrePhysical(anchor, 2);
            ArmThenHold(manager, closeCenter, startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Fact]
    public void TheConstructorDefault_IsTheProductionTester()
    {
        // Fixup 01 M2: an absent argument IS the production NoteHitTester — deleting the DI
        // wiring cannot silently switch the own-window conjunct off. The sentinel is the only way
        // to opt out, and it is visible at every call site. Mutant: the fallback swapped for the
        // sentinel (or the default dropped and null passed) → this and the DI pin go red.
        using var manager = new NoteManager(new WeakReferenceMessenger());
        Check.That(manager.HitTesterForTest).IsInstanceOf<NoteHitTester>();
    }

    // ── P2 D6: the floor and the geometry opt-out, through the real router ──

    [Fact]
    public void AShorteningMap_CannotTakeNotesBelowTheSixHundredMsFloor()
    {
        // The full production chain: registration (MinDwellMs 600) → DwellTimeRouter (clamps the
        // forgiving fill) → the detector's live dwell. The forgiving knobs ask for 200 ms; the
        // router must clamp to 600, so a 599 ms hold does not fire but 600 ms does.
        var settings = new DwellSettings();
        var adaptation = new AdaptationSettings { Enabled = true };
        var provider = new EffectiveSettingsProvider(adaptation);
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);
        provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.0); // maximum forgiving deflection

        var messenger = new WeakReferenceMessenger();
        var registry = new DwellDetectorRegistry();
        using var manager = new NoteManager(messenger, sink: null, dwellRegistry: registry, hitTester: InactiveNoteHitTester.Instance);
        var anchor = new PhysicalPoint(1600, 1000);
        manager.AddNoteTargetsForTest(anchor);
        var closeCenter = ButtonCentrePhysical(anchor, 2);

        using var router = new DwellTimeRouter(provider, registry); // applies the map on construction

        Arm(manager, closeCenter, startUs: 0);
        // 599 ms of held gaze — under the floor: must not fire.
        for (long t = 300_000; t <= 890_000; t += 33_000) manager.OnGazeMoved(closeCenter, t);
        Check.WithCustomMessage("the routed map must not shorten the note dwell below 600 ms")
            .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
        // One more step crosses 600 ms of held dwell: it fires — into the chip (P2 D5).
        manager.OnGazeMoved(closeCenter, 910_000);
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
    }

    [Fact]
    public void AGeometryMap_LeavesTheNoteHitCellsUnchanged()
    {
        // P2 D6: the named no-op applier. A geometry map pushed through the registration's
        // ApplyGeometry must change nothing — a margin would push hit cells outside their own
        // window (P2 D2's layout rule 1). Mutant: wiring SetEffectiveGeometry instead → the
        // covering rects grow and this goes red.
        var messenger = new WeakReferenceMessenger();
        var registry = new DwellDetectorRegistry();
        using var manager = new NoteManager(messenger, sink: null, dwellRegistry: registry, hitTester: InactiveNoteHitTester.Instance);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);
            var before = manager.LiveTargetRects().ToList();

            registry.Registrations[0].ApplyGeometry(extent => extent * 2);

            var after = manager.LiveTargetRects();
            Check.WithCustomMessage("a geometry map must leave every note hit cell unchanged")
                .That(after.Count).IsEqualTo(before.Count);
            for (int i = 0; i < before.Count; i++)
            {
                Check.That(after[i].X).IsCloseTo(before[i].X, 1e-9);
                Check.That(after[i].Y).IsCloseTo(before[i].Y, 1e-9);
                Check.That(after[i].Width).IsCloseTo(before[i].Width, 1e-9);
                Check.That(after[i].Height).IsCloseTo(before[i].Height, 1e-9);
            }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── P2 D7: the chrome rects ──

    [Fact]
    public void ChromeRects_ReturnTheCardBodyAndTheThreeCells()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            Check.That(manager.ChromeRects()).IsEmpty();

            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);

            var rects = manager.ChromeRects();
            Check.WithCustomMessage("one card body + three control cells per note")
                .That(rects).HasSize(4);

            double winLeftLogical = DpiHelper.PhysicalToLogicalX(anchor.X) - LayoutConstants.NoteWindowWidth / 2;
            double winTopLogical = DpiHelper.PhysicalToLogicalY(anchor.Y) - LayoutConstants.NoteAnchorOffsetY;
            var expectedCard = new PhysicalRect(
                DpiHelper.LogicalToPhysicalX(winLeftLogical),
                DpiHelper.LogicalToPhysicalY(winTopLogical),
                DpiHelper.LogicalToPhysicalX(LayoutConstants.NoteWindowWidth),
                DpiHelper.LogicalToPhysicalY(LayoutConstants.NoteWindowHeight));
            Check.That(rects[0]).IsEqualTo(expectedCard);

            double radius = DpiHelper.LogicalToPhysicalX(LayoutConstants.NoteButtonRadius);
            for (int slot = 0; slot < 3; slot++)
            {
                var (lx, ly) = NoteLayout.ButtonCenter(slot);
                var expectedCell = new PhysicalRect(
                    DpiHelper.LogicalToPhysicalX(winLeftLogical + lx) - radius,
                    DpiHelper.LogicalToPhysicalY(winTopLogical + ly) - radius,
                    radius * 2, radius * 2);
                Check.That(rects[1 + slot]).IsEqualTo(expectedCell);
            }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Fact]
    public void Compose_CarriesTheNoteZones_WithTheOtherSurfaces()
    {
        // P2 D7: the note zones ride the composition unconditionally — the card and cell rects in,
        // and (when P3 adds it) the tail stays out, because ChromeRects never reports one.
        var toolbar = new PhysicalRect(695, 0, 2450, 288);
        var noteCard = new PhysicalRect(1000, 500, 560, 488);
        var noteCell = new PhysicalRect(1480, 560, 120, 120);

        var zones = ActivationChromeZones.Compose(
            true, toolbar, false, null, null, null, new[] { noteCard, noteCell });

        Check.That(zones).ContainsExactly(toolbar, noteCard, noteCell);
        Check.That(ActivationChromeZones.Compose(false, default, false, null, null, null, new[] { noteCard }))
            .ContainsExactly(noteCard);
    }

    [Fact]
    public void CollectFromProduction_ReadsTheLiveManagerChromeRects()
    {
        // The production collector pulls the zones from the context's manager (null-safe).
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);
        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            Settings = settings,
            Messenger = messenger,
            KeyboardVM = new KeyboardViewModel(settings, messenger),
            ActionMenuVM = new ActionMenuViewModel(settings, messenger),
            ModesMenuVM = new ModesMenuViewModel(settings, messenger),
            OverlayManager = TestFakes.MakeOverlayManager(toolbar: new TestFakes.FakeToolbarOverlay()),
            NoteManager = manager,
        };

        Check.That(ActivationChromeZones.CollectFromProduction(context)).IsEmpty();

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.AddNoteTargetsForTest(new PhysicalPoint(1600, 1000));
            var zones = ActivationChromeZones.CollectFromProduction(context);
            Check.WithCustomMessage("the collector must include the live note chrome zones")
                .That(zones.Length).IsEqualTo(4);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── H2 and H6 (T1 review-03's missing latch tests) ──

    [Fact]
    public void TwoShortExcursions_WithAReturnInside_NeverReleaseTheLatchedTarget()
    {
        // H2: the leave clock RESTARTS on a return inside (fixup F1's hysteresis). Two outside
        // excursions shorter than 150 ms each must not sum to a release — the held gaze that came
        // back must not fire the control it never left. P2 D8 moved ✕ to slot 2, so the Display
        // control re-registered into Done's circle is ✎; firing it would re-enter Edit.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = manager.AddEditingNoteTargetsForTest(anchor);
            manager.Apply(new NoteAppend("hello"));
            var slot0 = ButtonCentrePhysical(anchor, 0);
            var outPoint = new PhysicalPoint(slot0.X + 200, slot0.Y); // outside the 60 px circle

            for (long t = 0; t <= 700_000; t += 33_000) manager.OnGazeMoved(slot0, t);
            Check.That(manager.IsEditing).IsFalse(); // Done fired; ✎ latched under the held gaze

            // Excursion 1: 100 ms out (< 150), then back inside for 100 ms.
            manager.OnGazeMoved(outPoint, 800_000);
            manager.OnGazeMoved(outPoint, 900_000);
            manager.OnGazeMoved(slot0, 1_000_000);
            // Excursion 2: 100 ms out again, then back.
            manager.OnGazeMoved(outPoint, 1_100_000);
            manager.OnGazeMoved(outPoint, 1_200_000);
            manager.OnGazeMoved(slot0, 1_300_000);

            // Held on the spot for a full second: the latch must still hold it.
            for (long t = 1_400_000; t <= 2_400_000; t += 100_000)
                manager.OnGazeMoved(slot0, t);
            Check.WithCustomMessage(
                    "two sub-150 ms excursions separated by a return must not release the latched ✎ — "
                    + "the leave clock restarts on every return inside")
                .That(manager.IsEditing).IsFalse();
            Check.That(manager.ActiveNotes).HasSize(1);
            Check.That(manager.ActiveNotes[0].Id).IsEqualTo(id);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void AnEmptyNewNotesDone_StillLatchesTheNeighbourControl_UnderTheHeldGaze()
    {
        // H6 (T1 fixup F2's scope): pin a new note exactly over a neighbour's Display control
        // (Done and the neighbour's ✎ share slot 0 even after P2 D8's reorder), dwell its Done —
        // the empty new note disappears, and the neighbour's ✎, under the same held gaze, must
        // come back LATCHED, not live.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            // A REAL window: the liveness half dwells the neighbour's ✎, and StartEdit drives
            // the note's window.
            var neighbourId = TypeIntoNewSavedNote(manager, "neighbour", anchor);
            var slot0 = ButtonCentrePhysical(anchor, 0);

            // The gaze settles on the spot before the pin, so the new note's Done is born latched.
            for (long t = 0; t <= 200_000; t += 100_000) manager.OnGazeMoved(slot0, t);

            manager.CreateNoteAt(anchor); // the new note's Done sits exactly on the neighbour's ✎
            Check.That(manager.IsEditing).IsTrue();

            // Leave past the 150 ms release, return, and save-by-Done: an EMPTY new note
            // disappears (nothing was ever saved).
            var away = new PhysicalPoint(slot0.X + 10_000, slot0.Y);
            for (long t = 300_000; t <= 600_000; t += 100_000) manager.OnGazeMoved(away, t);
            for (long t = 700_000; t <= 1_500_000; t += 100_000) manager.OnGazeMoved(slot0, t);
            Check.WithCustomMessage("the empty new note's Done must have fired and removed it")
                .That(manager.ActiveNotes.Select(n => n.Id).Contains(neighbourId)).IsTrue();
            Check.That(manager.ActiveNotes).HasSize(1);
            Check.That(manager.IsEditing).IsFalse();

            // The neighbour's ✎ is back, under the still-held gaze: a full second must not fire.
            for (long t = 1_600_000; t <= 2_600_000; t += 100_000) manager.OnGazeMoved(slot0, t);
            Check.WithCustomMessage(
                    "the empty-new exit must latch every target under the held gaze — the neighbour's "
                    + "✎ must not pull the saved note into Edit under eyes that never left")
                .That(manager.IsEditing).IsFalse();

            // Liveness: a leave and a return release the latch, and the ✎ works.
            for (long t = 2_700_000; t <= 2_900_000; t += 100_000) manager.OnGazeMoved(away, t);
            for (long t = 3_000_000; t <= 3_800_000; t += 100_000) manager.OnGazeMoved(slot0, t);
            Check.That(manager.IsEditing).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── P2 D9 (gate part): lifecycle ──

    [Fact]
    public void Dispose_UnsubscribesTheGateMessages_AndIgnoresLateOnes()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);
        manager.AddNoteTargetsForTest(new PhysicalPoint(1600, 1000));
        manager.Dispose();

        // A late flag update must neither throw nor resurrect the disposed manager's state.
        Check.ThatCode(() =>
        {
            messenger.Send(new EyeCareExerciseActiveMessage(true));
            messenger.Send(new CalibrationStateChangedMessage(CalibrationState.Collecting, 0));
        }).DoesNotThrow();
    }

    // ── the one non-headless test (P2 Risks): real windows, real WindowFromPoint ──
    // Opt-in like the app-launching FlaUI tests: it needs an interactive desktop session.

    [StaFact]
    [Trait("Category", "UI")]
    public void TheRealWindowFromPoint_PutsTheTopmostNoteUnderTheGaze()
    {
        Fixtures.AppFixture.SkipIfDisabled();

        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, sink: null, dwellRegistry: null,
            hitTester: new NoteHitTester());

        // This one needs REAL HWNDs: the gate test drives WindowFromPoint. The App test assembly
        // switches RealNoteWindows off, so switch it back on for this test only.
        var wasReal = NoteManager.RealNoteWindows;
        NoteManager.RealNoteWindows = true;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchorBottom = new PhysicalPoint(1600, 1000);
            var anchorTop = new PhysicalPoint(1640, 1000);
            var bottomId = TypeIntoNewSavedNote(manager, "bottom", anchorBottom);
            var topId = TypeIntoNewSavedNote(manager, "top", anchorTop);
            var bottomHandle = new WindowInteropHelper(manager.WindowForTest(bottomId)!).EnsureHandle();
            var topHandle = new WindowInteropHelper(manager.WindowForTest(topId)!).EnsureHandle();
            var bottomClose = ButtonCentrePhysical(anchorBottom, 2);

            // The real hit test must resolve the gaze to one of the two note windows.
            var root = new NoteHitTester().RootWindowAt(bottomClose);
            Check.WithCustomMessage("WindowFromPoint must land on one of the two shown note windows")
                .That(new[] { bottomHandle, topHandle }.Contains(root)).IsTrue();

            // The gaze rests on the bottom note's ✕, but the TOP window (shown last) is hit-testably
            // on top: the bottom ✕ — strictly nearest — must be filtered, and the top one takes the
            // dwell (P2 D5: into its chip).
            ArmThenHold(manager, bottomClose, startUs: 0);
            Check.WithCustomMessage("the bottom note's ✕ must never fire under the top window")
                .That(manager.ActiveNotes.First(n => n.Id == bottomId).State).IsEqualTo(NoteState.Display);
            Check.WithCustomMessage("the topmost window's ✕ takes the dwell")
                .That(manager.ActiveNotes.First(n => n.Id == topId).State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { NoteManager.RealNoteWindows = wasReal; DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── helpers ──

    /// <summary>Creates a note and types <paramref name="text"/> into it, mirroring the
    /// NoteManagerTests helper — the only way text enters a note in P1 — then commits it with
    /// Done. Leaves the note IN DISPLAY with its saved text. T2 review-04's helper split: the
    /// name says the state it leaves, so a test never assumes the wrong one.</summary>
    private static string TypeIntoNewSavedNote(NoteManager manager, string text, PhysicalPoint? point = null)
    {
        manager.CreateNoteAt(point ?? new PhysicalPoint(0, 0));
        manager.Apply(new NoteAppend(text));
        manager.FinishEdit(manager.ActiveNotes[^1].Id, commit: true);
        return manager.ActiveNotes[^1].Id;
    }

    /// <summary>A dwell button's centre in physical px, through the SAME math the manager uses.</summary>
    private static PhysicalPoint ButtonCentrePhysical(PhysicalPoint anchor, int slot)
    {
        double winLeftLogical = DpiHelper.PhysicalToLogicalX(anchor.X) - LayoutConstants.NoteWindowWidth / 2;
        double winTopLogical = DpiHelper.PhysicalToLogicalY(anchor.Y) - LayoutConstants.NoteAnchorOffsetY;
        var (lx, ly) = NoteLayout.ButtonCenter(slot);
        return new(
            DpiHelper.LogicalToPhysicalX(winLeftLogical + lx),
            DpiHelper.LogicalToPhysicalY(winTopLogical + ly));
    }

    /// <summary>Arms ✕ (a 200 ms excursion outside its circle, past the 150 ms release) and then
    /// holds the gaze on it for 900 ms — enough for a fresh 600 ms dwell to complete.</summary>
    private static void ArmThenHold(NoteManager manager, PhysicalPoint closeCenter, long startUs)
    {
        Arm(manager, closeCenter, startUs);
        for (long t = startUs + 300_000; t <= startUs + 1_200_000; t += 100_000)
            manager.OnGazeMoved(closeCenter, t);
    }

    private static void Arm(NoteManager manager, PhysicalPoint closeCenter, long startUs)
    {
        for (long t = startUs; t <= startUs + 200_000; t += 100_000)
            manager.OnGazeMoved(new PhysicalPoint(closeCenter.X + 10_000, closeCenter.Y), t);
    }

    /// <summary>Recording <see cref="INoteHitTester"/> fake: the test decides which HWND the gaze
    /// is over, per sample.</summary>
    private sealed class FakeHitTester : INoteHitTester
    {
        public IntPtr Answer { get; set; }
        public IntPtr RootWindowAt(PhysicalPoint point) => Answer;
    }
}
