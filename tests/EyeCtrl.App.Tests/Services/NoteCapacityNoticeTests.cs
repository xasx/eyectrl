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
}
