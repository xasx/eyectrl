namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

/// <summary>
/// The note gate's occluder WIRING (T2 fixup 02, review-02 B1): the four production delegates
/// live in <see cref="NoteOccluderWiring"/> and are executed here against the REAL container —
/// the reviewer's R7/R7b probes, adopted. R7b's original failure mode (the inlined App lambda
/// resolved <c>ITrackStatusOverlay</c>, which the container does not register, so every gaze
/// sample threw while a note existed) is pinned two ways: the production wiring runs on a real
/// provider without throwing, and the track-status source demonstrably CONNECTS (a visible fake
/// card occludes a held ✕). Fail-closed: a throwing source is contained, logged once per
/// episode, and the sample counts as occluded.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteOccluderWiringTests
{
    private static readonly string AppData = TestAppDataDir.Create("note-occluder-wiring");

    private static ServiceProvider BuildProvider() =>
        AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);

    /// <summary>The exact production context shape the wiring reads: the manager itself plus an
    /// overlay manager carrying the track-status source.</summary>
    private static (NoteManager Manager, EventCoordinationContext Context, TestFakes.FakeTrackStatusOverlay TrackStatus)
        MakeContext(ServiceProvider sp, NoteManager manager, TestFakes.FakeTrackStatusOverlay trackStatus)
    {
        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(trackStatus: trackStatus),
            NoteManager = manager,
        };
        return (manager, context, trackStatus);
    }

    // R7b (reviewer probe, adopted) + review-03 M1's POSITIVE CONTROL: the exact production
    // wiring, the real container, one Display note, the track-status card hidden — after the
    // leave-before-arm, ✕ MUST FIRE through the real wiring, and the run must log NO occluder
    // failure. Mutant W1 (the B1 lambda restored inside Wire) makes the track-status read throw
    // on every sample: fail-closed then holds ✕ forever AND logs — both halves go red. A bare
    // "does not throw" assertion is vacuous here, because fail-closed contains the throw.
    [StaFact]
    public void TheProductionWiring_LetsCloseFire_OnTheRealContainer_WithoutLoggingAnySourceFailure()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Error()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            using var sp = BuildProvider();
            var manager = (NoteManager)sp.GetRequiredService<INoteManager>();
            var trackStatus = new TestFakes.FakeTrackStatusOverlay(); // hidden: Visible stays false
            var (_, context, _) = MakeContext(sp, manager, trackStatus);

            NoteOccluderWiring.Wire(manager, sp, context);

            DpiHelper.Initialize(2.0, 2.0);
            try
            {
                var anchor = new PhysicalPoint(1600, 1000);
                manager.AddNoteTargetsForTest(anchor);
                var closeCenter = ButtonCentrePhysical(anchor, 2);

                // Leave-before-arm: 300 ms outside the circle, then a 900 ms hold on ✕.
                for (long t = 0; t <= 300_000; t += 100_000)
                    manager.OnGazeMoved(new PhysicalPoint(closeCenter.X + 10_000, closeCenter.Y), t);
                for (long t = 400_000; t <= 1_200_000; t += 100_000)
                    manager.OnGazeMoved(closeCenter, t);

                Check.WithCustomMessage(
                        "✕ must FIRE through the real production wiring on the real container — a "
                        + "silently-failing occluder source (the B1 regression) holds it forever; "
                        + "since P2 D5 the fired ✕ enters the chip instead of deleting")
                    .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

                var failures = sink.Events
                    .Where(e => e.MessageTemplate.Text.Contains("occluder source threw", StringComparison.Ordinal))
                    .ToList();
                Check.WithCustomMessage(
                        "the production wiring on the real container must log NO occluder source "
                        + "failure; logged: " + string.Join(" | ", sink.Events.Select(e => e.RenderMessage())))
                    .That(failures).IsEmpty();
            }
            finally { DpiHelper.Initialize(1.0, 1.0); }
        }
        finally { Log.Logger = previousLogger; }
    }

    // Required (4): each of the four sources resolves through the real container (or, for the
    // track-status card, through the coordination context's overlay manager — the window is
    // OverlayManager-owned and deliberately NOT a DI service, which is exactly what B1 got
    // wrong). The lag bridge reads empty before App attaches the instrumented host; the two
    // notice presenters resolve (constructing their real windows) and report null while hidden.
    [StaFact]
    public void EachOccluderSource_ResolvesThroughTheRealContainer()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var sp = BuildProvider();

        var lagMarkBridge = sp.GetRequiredService<LagMarkBridge>();
        Check.That(lagMarkBridge.LiveTargetRects()).IsEmpty();

        var drift = sp.GetRequiredService<IDriftNoticePresenter>();
        Check.WithCustomMessage("the drift notice is hidden in a fresh session")
            .That(drift.TryGetVisibleBounds(out _)).IsFalse();

        var alert = sp.GetRequiredService<ICaregiverAlertPresenter>();
        Check.WithCustomMessage("the caregiver alert is hidden in a fresh session")
            .That(alert.TryGetVisibleBounds(out _)).IsFalse();

        // The track-status card's owner resolves; the wiring reads it from the CONTEXT's
        // overlay manager, which the production context always carries.
        Check.That(sp.GetRequiredService<IOverlayManager>()).IsNotNull();
    }

    // The wiring must CONNECT, not merely not-throw: with the fake card visible over ✕, the
    // production delegate (container → presenter seam → manager) holds the destructive dwell.
    [StaFact]
    public void TheWiredTrackStatusSource_OccludesThroughTheRealWiring()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var sp = BuildProvider();
        var manager = (NoteManager)sp.GetRequiredService<INoteManager>();
        var trackStatus = new TestFakes.FakeTrackStatusOverlay();
        var (_, context, _) = MakeContext(sp, manager, trackStatus);

        NoteOccluderWiring.Wire(manager, sp, context);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);
            var closeCenter = ButtonCentrePhysical(anchor, 2);

            // The card sits exactly over ✕, visible: a full hold must not fire.
            trackStatus.Visible = true;
            trackStatus.Bounds = new PhysicalRect(closeCenter.X - 300, closeCenter.Y - 100, 600, 200);
            for (long t = 0; t <= 1_500_000; t += 100_000)
                manager.OnGazeMoved(closeCenter, t);
            Check.WithCustomMessage("the wired track-status source must occlude ✕ beneath the card")
                .That(manager.ActiveNotes).HasSize(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // Fixup 02 (3): a throwing source fails CLOSED — no exception escapes the gaze path, the
    // sample counts as occluded so no note control fires, and the failure is logged once per
    // source per episode (a recovered source re-arms both the gate and the log).
    [StaFact]
    public void AThrowingOccluderSource_FailsClosed_LogsOnce_AndOpensOnRecovery()
    {
        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);
        bool throwOnRead = true;
        manager.SetOccluderSources(driftNoticeBounds: () =>
        {
            if (throwOnRead) throw new InvalidOperationException("wiring slip");
            return null;
        });

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Error()
                .WriteTo.Sink(sink)
                .CreateLogger();
            try
            {
                var anchor = new PhysicalPoint(1600, 1000);
                manager.AddNoteTargetsForTest(anchor);
                var closeCenter = ButtonCentrePhysical(anchor, 2);

                // A 2 s hold on ✕ with the source throwing on every sample: nothing escapes,
                // and the destructive dwell can never complete (fail closed).
                Check.ThatCode(() =>
                {
                    for (long t = 0; t <= 2_000_000; t += 100_000)
                        manager.OnGazeMoved(closeCenter, t);
                }).DoesNotThrow();
                Check.WithCustomMessage("a throwing occluder source must hold the note (fail closed)")
                    .That(manager.ActiveNotes).HasSize(1);

                var occluderErrors = sink.Events
                    .Where(e => e.MessageTemplate.Text.Contains("occluder source threw", StringComparison.Ordinal))
                    .ToList();
                Check.WithCustomMessage(
                        "the failure must be logged ONCE per source per episode, not once per sample; "
                        + "logged: " + string.Join(" | ", sink.Events.Select(e => e.RenderMessage())))
                    .That(occluderErrors).HasSize(1);
                Check.WithCustomMessage("the log must name the failing source")
                    .That(occluderErrors.Single()
                        .Properties.TryGetValue("OccluderSource", out var source)
                        && source is ScalarValue { Value: string name }
                        && name == NoteManager.KnownOccluderSources.DriftNotice).IsTrue();

                // Recovery: the source heals, the gate re-opens, and a fresh failure is loud again.
                throwOnRead = false;
                for (long t = 2_100_000; t <= 2_300_000; t += 100_000)
                    manager.OnGazeMoved(new PhysicalPoint(closeCenter.X + 10_000, closeCenter.Y), t);
                throwOnRead = true;
                for (long t = 2_400_000; t <= 2_600_000; t += 100_000)
                    manager.OnGazeMoved(new PhysicalPoint(closeCenter.X + 10_000, closeCenter.Y), t);
                var episodes = sink.Events
                    .Count(e => e.MessageTemplate.Text.Contains("occluder source threw", StringComparison.Ordinal));
                Check.WithCustomMessage("a re-failed source must be logged again (episode re-armed)")
                    .That(episodes).IsEqualTo(2);

                // And the gate itself: the source healthy, the leave satisfied, ✕ fires — into
                // the chip (P2 D5).
                throwOnRead = false;
                for (long t = 2_700_000; t <= 3_500_000; t += 100_000)
                    manager.OnGazeMoved(closeCenter, t);
                Check.WithCustomMessage("a recovered source must not keep the gate shut")
                    .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            }
            finally
            {
                Log.Logger = previousLogger;
            }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>Captures the events NoteManager logs while a test drives it (the
    /// ActivationOfferCoordinatorTests pattern). Assertions read the structured properties.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    // Fixup 03 m1: a throwing occluder source answers "occluded" for every target EXCEPT
    // {id}:done. Done is non-destructive and P1's only Edit exit — a broken source must never
    // trap the owner in Edit with the keyboard captured. Done still respects every working
    // source, the keyboard gate, the own-window conjunct, suspend and pause; the destructive
    // ✕ under the same failure must not fire. Mutant: drop the exemption from GateOpenFor →
    // Done never fires and this goes red.
    //
    // Review-04 m2: "Done and ONLY Done" is a pinned contract before T3 puts Discard next to
    // Done. ⌨ exists here (the keyboard is hidden, so slot 1 registers) and a held ⌨ under the
    // same throwing source must send NO KeyboardShowRequestedMessage. Mutant W8 (also exempt
    // ":keyboard") turns that assertion red. T3's ↩ Discard gets the same pin in its own tests.
    [Fact]
    public void AThrowingOccluderSource_LetsDoneFire_ButKeepsCloseGated()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = false }; // hidden: ⌨ registers in slot 1
        manager.SetKeyboardProbe(probe);
        manager.SetOccluderSources(driftNoticeBounds: () =>
            throw new InvalidOperationException("wiring slip"));

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddEditingNoteTargetsForTest(anchor);
            manager.Apply(new NoteAppend("hello"));
            // The counter starts AFTER the Edit entry: that entry itself summons the hidden
            // keyboard (P1 D5), which is not the behaviour under test.
            int showRequests = 0;
            messenger.Register<object, KeyboardShowRequestedMessage>(this, (_, _) => showRequests++);
            var slot0 = ButtonCentrePhysical(anchor, 0); // ✓ Done in Edit — and ✕'s slot in Display
            var slot1 = ButtonCentrePhysical(anchor, 1); // ⌨ Show keyboard (Edit only)

            // The exemption must exempt nothing but Done: a held ⌨ — while still editing, so it
            // exists — under the throwing source stays gated and must never re-summon the
            // keyboard (mutant W8).
            for (long t = 0; t <= 1_200_000; t += 100_000)
                manager.OnGazeMoved(slot1, t);
            Check.WithCustomMessage(
                    "the exemption is Done and ONLY Done — a held ⌨ under a throwing source must "
                    + "not fire and must send no KeyboardShowRequestedMessage")
                .That(showRequests).IsEqualTo(0);
            Check.WithCustomMessage("⌨ must not have ended the Edit either")
                .That(manager.IsEditing).IsTrue();

            // The held gaze on a throwing source: Done must still fire and SAVE the note.
            for (long t = 1_300_000; t <= 2_500_000; t += 100_000)
                manager.OnGazeMoved(slot0, t);
            Check.WithCustomMessage(
                    "a throwing occluder source must not trap the owner in Edit — Done is the only "
                    + "Edit exit and the exemption exists so it stays reachable")
                .That(manager.IsEditing).IsFalse();
            Check.WithCustomMessage("Done must have SAVED the note (non-destructive exit)")
                .That(manager.ActiveNotes[0].Text).IsEqualTo("hello");

            // The same held gaze now rests on ✕ (slot 2, P2 D8 — re-registered fresh by the
            // exit): the destructive control must stay gated by the fail-closed answer.
            var slot2 = ButtonCentrePhysical(anchor, 2); // ✕ Close in Display
            for (long t = 2_600_000; t <= 3_600_000; t += 100_000)
                manager.OnGazeMoved(slot2, t);
            Check.WithCustomMessage(
                    "a throwing occluder source must keep the destructive ✕ gated under the same "
                    + "held gaze that could still reach Done")
                .That(manager.ActiveNotes).HasSize(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // Review-04 m1: a failing occluder source is NO INFORMATION for the leave-before-arm timers
    // (NoteManager's timer guard skips failing samples). The difference only shows after the
    // source recovers — the mutant W6 would have released the latch on the failing excursion, so
    // the held return fires ✕ 600 ms later; the real code keeps it latched until a HEALTHY leave.
    // The latch here is the real destructive target: the gaze parks on ✕ (slot 2, P2 D8) and a
    // neighbour's removal re-registers the survivor's targets under the held gaze.
    [Fact]
    public void AFailingOccluderSource_NeverReleasesALatch_OnlyAHealthyLeaveDoes()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance);
        bool throwOnRead = false;
        manager.SetOccluderSources(driftNoticeBounds: () =>
        {
            if (throwOnRead) throw new InvalidOperationException("wiring slip");
            return null;
        });

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var idA = manager.AddNoteTargetsForTest(anchor);
            var idB = manager.AddNoteTargetsForTest(new PhysicalPoint(3000, 2000));
            var closeCenter = ButtonCentrePhysical(anchor, 2); // ✕ (P2 D8)
            var away = new PhysicalPoint(closeCenter.X + 10_000, closeCenter.Y);

            // 1. Latch ✕ under a held gaze: the gaze settles on ✕, then a neighbour's removal
            //    re-registers the survivor's Display targets — the ✕ under the eyes latches.
            for (long t = 0; t <= 200_000; t += 100_000) manager.OnGazeMoved(closeCenter, t);
            manager.RemoveNote(idB);
            Check.That(manager.ActiveNotes.Select(n => n.Id)).ContainsExactly(idA);

            // 2. The source fails; 3. the gaze leaves for 300 ms — failing samples carry no
            //    leave information, so the latch must survive this excursion.
            throwOnRead = true;
            for (long t = 300_000; t <= 600_000; t += 100_000) manager.OnGazeMoved(away, t);

            // 4. The source heals; 5. the gaze returns and holds a full second: the latch is
            //    still on — ✕ must not fire, and with P2 D5 that is observable: the note must
            //    still be a plain Display card, NOT a chip.
            throwOnRead = false;
            for (long t = 700_000; t <= 1_700_000; t += 100_000) manager.OnGazeMoved(closeCenter, t);
            Check.WithCustomMessage(
                    "a failing source's excursion must not release the latch — only a HEALTHY "
                    + "leave may (review-04 m1, mutant W6)")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.ActiveNotes.Select(n => n.Id)).Contains(idA);

            // Positive control: a healthy 300 ms leave releases the latch, and the return fires ✕
            // — into the chip (P2 D5).
            for (long t = 1_800_000; t <= 2_100_000; t += 100_000) manager.OnGazeMoved(away, t);
            for (long t = 2_200_000; t <= 3_100_000; t += 100_000) manager.OnGazeMoved(closeCenter, t);
            Check.WithCustomMessage(
                    "after a healthy leave the latch must release — the held ✕ must fire (the "
                    + "guard must not be always-on)")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>A dwell button's centre in physical px, through the SAME math the manager uses.</summary>
    private static PhysicalPoint ButtonCentrePhysical(PhysicalPoint anchor, int slot)
    {
        double winLeftLogical = DpiHelper.PhysicalToLogicalX(anchor.X) - EyeCtrl.Core.LayoutConstants.NoteWindowWidth / 2;
        double winTopLogical = DpiHelper.PhysicalToLogicalY(anchor.Y) - EyeCtrl.Core.LayoutConstants.NoteAnchorOffsetY;
        var (lx, ly) = EyeCtrl.App.Overlays.NoteLayout.ButtonCenter(slot);
        return new(
            DpiHelper.LogicalToPhysicalX(winLeftLogical + lx),
            DpiHelper.LogicalToPhysicalY(winTopLogical + ly));
    }
}
