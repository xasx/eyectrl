namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;

using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using System.Windows;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The capacity notice (P4 D4): at capacity the pin SHOWS it instead of staying silent, the
/// notice is placed clear of every card and chip, and while it shows it is an OCCLUDER - a gaze
/// resting on the notice never fires a note cell beneath it. The wiring rides
/// <see cref="NoteOccluderWiring"/> (the composition-root path the other four sources use).
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteCapacityNoticeTests
{
    private static void NoRealNotice(NoteManager manager) => manager.NoticeWindowShowsForTest = false;
    [StaFact]
    public void AtCapacity_TheNoticeShows_AndNoWindowIsCreated()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        for (int i = 0; i < 8; i++)
            manager.AddNoteTargetsForTest(new PhysicalPoint(400 + (i % 4) * 600, i < 4 ? 400 : 1000));
        Check.That(manager.IsAtCapacity).IsTrue();

        manager.NoticeWindowShowsForTest = false;
        manager.ShowCapacityNotice(new PhysicalPoint(1600, 1000));

        Check.WithCustomMessage("the notice reports its visible bounds while showing")
            .That(manager.TryGetNoticeVisibleBounds(out var bounds)).IsTrue();
        Check.WithCustomMessage("the notice rect is on screen and of the notice size")
            .That(bounds.Width > 0 && bounds.Height > 0).IsTrue();
    }

    [StaFact]
    public void TheNoticeRect_IntersectsNoCard_WhenAFreeSpotExists()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
        for (int i = 0; i < 8; i++)
            manager.AddNoteTargetsForTest(new PhysicalPoint(400 + (i % 4) * 600, i < 4 ? 400 : 1000));

        // A free spot: the bottom-right of the work area holds no note.
        manager.NoticeWindowShowsForTest = false;
        manager.ShowCapacityNotice(new PhysicalPoint(3400, 2000));

        Check.That(manager.TryGetNoticeVisibleBounds(out var notice)).IsTrue();
        var noticeDip = new Rect(
            DpiHelper.PhysicalToLogicalX(notice.X),
            DpiHelper.PhysicalToLogicalY(notice.Y),
            DpiHelper.PhysicalToLogicalX(notice.Width),
            DpiHelper.PhysicalToLogicalY(notice.Height));
        foreach (var note in manager.ActiveNotes)
        {
            var card = manager.PlacementForTest(note.Id)!.Card;
            Check.WithCustomMessage($"the notice {noticeDip} must not cover note card {card}")
                .That(noticeDip.IntersectsWith(card)).IsFalse();
        }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Fact]
    public void AGazeRestingOnTheNotice_NeverFiresTheCellBeneathIt()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        var anchor = new PhysicalPoint(1600, 1000);
        var id = manager.AddNoteTargetsForTest(anchor);
        var close = NoteTestBench.CellCentre(manager, id, 3);

        // The notice covers the close cell (what the clamped fallback can produce).
        manager.SetOccluderSources(noteNoticeBounds: () => new PhysicalRect(
            close.X - 400, close.Y - 200, 800, 400));

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            for (long t = 0; t <= 1_500_000; t += 100_000)
                manager.OnGazeMoved(close, t);
            Check.WithCustomMessage("the notice occludes the close cell beneath it")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static readonly string AppData = TestAppDataDir.Create("note-capacity-notice");

    private static ServiceProvider BuildProvider() =>
        AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);

    // The composition-root pin: the real wiring connects the manager-owned notice so the gate's
    // occluder source is the live window's visible bounds - the same shape NoteOccluderWiringTests
    // pins for the four external sources.
    [StaFact]
    public void TheRealWiring_ConnectsTheNoticeSource_OnTheRealContainer()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var sp = BuildProvider();
        var manager = (NoteManager)sp.GetRequiredService<INoteManager>();
        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(trackStatus: new TestFakes.FakeTrackStatusOverlay()),
            NoteManager = manager,
        };

        NoteOccluderWiring.Wire(manager, sp, context);
        Check.WithCustomMessage("the wire must register the notice occluder source")
            .That(manager.NoticeBoundsForTest).IsNotNull();

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            Check.WithCustomMessage("the wired source answers false while the notice is hidden")
                .That(manager.NoticeBoundsForTest!().HasValue).IsFalse();

            manager.NoticeWindowShowsForTest = false;
        manager.ShowCapacityNotice(new PhysicalPoint(1600, 1000));
            Check.WithCustomMessage("the wired source answers the shown notice bounds")
                .That(manager.NoticeBoundsForTest!().HasValue).IsTrue();
            Check.That(manager.TryGetNoticeVisibleBounds(out var direct)).IsTrue();
            Check.That(manager.NoticeBoundsForTest!()!.Value).IsEqualTo(direct);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // The dispatcher's gating: at capacity the pin SHOWS the notice and creates no window; below
    // capacity it creates as before. Positive controls for the never-fires guard.
    [StaFact]
    public void ThePin_AtCapacity_ShowsTheNotice_AndCreatesNothing()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        for (int i = 0; i < 8; i++)
            manager.AddNoteTargetsForTest(new PhysicalPoint(400 + (i % 4) * 600, i < 4 ? 400 : 1000));

        var input = new TestFakes.FakeInputSimulator();
        var ctx = TestFakes.MakeContext(input) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(glassBackdrop: new TestFakes.FakeGlassBackdropOverlay()),
            TargetHighlightVM = new TargetHighlightOverlayViewModel(),
            NoteManager = manager,
        };
        var dispatcher = new KeyboardInteractionDispatcher();
        dispatcher.Initialize(ctx);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            Check.That(manager.TryGetNoticeVisibleBounds(out _)).IsFalse();
            dispatcher.HandleActionRequested(new ActionRequestedMessage(
                InteractionAction.PinNote, new PhysicalPoint(1600, 1000), IsPreciseTarget: true));

            Check.WithCustomMessage("at capacity the pin shows the notice")
                .That(manager.TryGetNoticeVisibleBounds(out _)).IsTrue();
            Check.WithCustomMessage("at capacity the pin creates no 9th window")
                .That(manager.ActiveNotes).HasSize(8);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void ThePin_BelowCapacity_CreatesAsBefore_AndNoNoticeShows()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        var input = new TestFakes.FakeInputSimulator();
        var ctx = TestFakes.MakeContext(input) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(glassBackdrop: new TestFakes.FakeGlassBackdropOverlay()),
            TargetHighlightVM = new TargetHighlightOverlayViewModel(),
            NoteManager = manager,
        };
        var dispatcher = new KeyboardInteractionDispatcher();
        dispatcher.Initialize(ctx);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            dispatcher.HandleActionRequested(new ActionRequestedMessage(
                InteractionAction.PinNote, new PhysicalPoint(1600, 1000), IsPreciseTarget: true));

            Check.WithCustomMessage("below capacity the pin creates")
                .That(manager.ActiveNotes).HasSize(1);
            Check.WithCustomMessage("no notice below capacity")
                .That(manager.TryGetNoticeVisibleBounds(out _)).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // Review-01 survivor, R13 adopted: the SAME hold fires the cell once the notice is gone.
    [Fact]
    public void R13_TheCellFires_OnceTheNoticeIsGone()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        var anchor = new PhysicalPoint(1600, 1000);
        var id = manager.AddNoteTargetsForTest(anchor);
        var close = NoteTestBench.CellCentre(manager, id, 3);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            PhysicalRect? notice = new PhysicalRect(close.X - 400, close.Y - 200, 800, 400);
            manager.SetOccluderSources(noteNoticeBounds: () => notice);

            for (long t = 0; t <= 1_500_000; t += 100_000)
                manager.OnGazeMoved(close, t);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);

            notice = null; // the notice faded out
            for (long t = 1_600_000; t <= 1_800_000; t += 100_000)
                manager.OnGazeMoved(new PhysicalPoint(close.X + 10_000, close.Y), t); // leave (latch release)
            for (long t = 1_900_000; t <= 4_000_000; t += 100_000)
                manager.OnGazeMoved(close, t);
            Check.WithCustomMessage("with the notice gone the same hold must fire the close cell")
                .That(manager.ActiveNotes.Count == 0 || manager.ActiveNotes[0].State != NoteState.Display).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // Review-01 survivor: the avoid list is EXERCISED - the pin lands beside an existing card,
    // so the notice must not merely sit in an empty corner.
    [StaFact]
    public void TheNotice_AvoidsACardThatIsInTheWay()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.AddNoteTargetsForTest(new PhysicalPoint(1600, 1000));
            manager.NoticeWindowShowsForTest = false;
            // The pin sits just BELOW the card: the preferred Right window (and Left) intersects
            // the card's band, so the avoid list must push the notice to the free Below side.
            manager.ShowCapacityNotice(new PhysicalPoint(1800, 1400));

            Check.That(manager.TryGetNoticeVisibleBounds(out var notice)).IsTrue();
            var noticeDip = new Rect(
                DpiHelper.PhysicalToLogicalX(notice.X),
                DpiHelper.PhysicalToLogicalY(notice.Y),
                DpiHelper.PhysicalToLogicalX(notice.Width),
                DpiHelper.PhysicalToLogicalY(notice.Height));
            var card = manager.PlacementForTest(manager.ActiveNotes[0].Id)!.Card;
            Check.WithCustomMessage($"the notice {noticeDip} must avoid the in-the-way card {card}")
                .That(noticeDip.IntersectsWith(card)).IsFalse();
            Check.WithCustomMessage("the avoid was exercised: the free side is Below")
                .That(NoticeIsBelowTheCard(manager)).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // Review-01 survivor: a notice over a pending chip STOPS its countdown.
    [Fact]
    public void ANoticeOverAChip_StopsItsClock()
    {
        var messenger = new WeakReferenceMessenger();
        var clock = new SurvivorsFakeClock();
        var timer = new SurvivorsFakeUndoTimer();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            clockMs: clock.Read, undoTimer: timer, workAreaDip: NoteTestBench.WorkAreaProvider);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // The scale FIRST, then the targets: the dwell cells register in physical px at the
            // live scale (the scale-1.0 registration is exactly what silently broke the first
            // draft of this test).
            var anchor = new PhysicalPoint(1600, 1000);
            var id = manager.AddNoteTargetsForTest(anchor);
            var tUs = 0L;
            void TickAt(PhysicalPoint sampleAt)
            {
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(sampleAt, tUs, IsInGap: false)));
                clock.Advance(100);
                timer.RaiseTick();
                tUs += 100_000;
            }

            var close = NoteTestBench.CellCentre(manager, id, 3);
            for (var t = 0L; t <= 200_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(new PhysicalPoint(close.X + 10_000, close.Y), t, IsInGap: false)));
            for (var t = 300_000L; t <= 1_500_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(close, t, IsInGap: false)));
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            var undo = NoteTestBench.CellCentre(manager, id, 0);

            // A notice over the Undo cell: no reachable time accumulates (the clock condition 4).
            // The tick samples park BESIDE the chip (valid + fresh, off every cell), so the dwell
            // never fires Undo while the countdown is under test.
            var parked = new PhysicalPoint(close.X + 10_000, close.Y);
            manager.SetOccluderSources(noteNoticeBounds: () => new PhysicalRect(undo.X - 400, undo.Y - 200, 800, 400));
            for (var i = 0; i < 100; i++) TickAt(parked);
            Check.WithCustomMessage("a notice over the chip must stop its countdown (the chip survives)")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            // The notice gone: the countdown completes and the Removed chip expires (gone).
            manager.SetOccluderSources(noteNoticeBounds: () => null);
            for (var i = 0; i < 90; i++) TickAt(parked);
            Check.WithCustomMessage("with the notice gone the chip clock completes (the Removed chip expires)")
                .That(manager.ActiveNotes).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // Review-01 survivor: the 3 s hold is real - fully shown just before, fading after.
    [StaFact]
    public void TheNoticeHolds_ThenFades()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        for (var i = 0; i < 8; i++)
            manager.AddNoteTargetsForTest(new PhysicalPoint(400 + (i % 4) * 600, i < 4 ? 400 : 1000));
        manager.NoticeWindowShowsForTest = false;
        manager.ShowCapacityNotice(new PhysicalPoint(1600, 1000));

        var notice = manager.NoticeWindowForTest!;
        Check.WithCustomMessage("the notice is fully shown right after it appears")
            .That(notice.IsFadingForTest).IsFalse();

        // Review-02 m1: the durations are PINNED - a 30 s hold or a 5 s fade must fail here.
        Check.WithCustomMessage($"the hold interval must be exactly 3 s, got {notice.HoldIntervalForTest.TotalSeconds:0.#} s")
            .That(notice.HoldIntervalForTest).IsEqualTo(TimeSpan.FromSeconds(3));
        Check.WithCustomMessage($"the fade must run exactly 1 s, got {notice.FadeDurationForTest.TimeSpan.TotalSeconds:0.#} s")
            .That(notice.FadeDurationForTest.TimeSpan).IsEqualTo(TimeSpan.FromSeconds(1));

        notice.OnHoldElapsedForTest();
        Check.WithCustomMessage("the notice fades once its 3 s hold expires")
            .That(notice.IsFadingForTest).IsTrue();
        Check.That(manager.TryGetNoticeVisibleBounds(out _)).IsTrue(); // still up during the fade
    }

    private static bool NoticeIsBelowTheCard(NoteManager manager)
    {
        manager.TryGetNoticeVisibleBounds(out var notice);
        var card = manager.PlacementForTest(manager.ActiveNotes[0].Id)!.Card;
        double centreY = DpiHelper.PhysicalToLogicalY(notice.Y + (notice.Height / 2));
        return centreY > card.Bottom;
    }

    internal sealed class SurvivorsFakeClock
    {
        private long _nowMs;
        public long Read() => _nowMs;
        public void Advance(long ms) => _nowMs += ms;
    }

    internal sealed class SurvivorsFakeUndoTimer : INoteUndoTimer
    {
        private Action? _tick;
        public bool IsRunning { get; private set; }
        public void Start(Action onTick) { _tick = onTick; IsRunning = true; }
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
        public void RaiseTick() => _tick?.Invoke();
    }
}
