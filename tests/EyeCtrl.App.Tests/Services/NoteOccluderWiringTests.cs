namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
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

    // R7b (reviewer probe, adopted): the exact production wiring, the real container, one note,
    // one gaze sample — no exception. On 8a6efb45 the inlined lambda's
    // GetRequiredService<ITrackStatusOverlay> threw InvalidOperationException here (RED).
    [StaFact]
    public void TheProductionOccluderWiring_RunsOnTheRealContainer_WithoutThrowing()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var sp = BuildProvider();
        var manager = (NoteManager)sp.GetRequiredService<INoteManager>();
        var trackStatus = new TestFakes.FakeTrackStatusOverlay();
        var (_, context, _) = MakeContext(sp, manager, trackStatus);

        NoteOccluderWiring.Wire(manager, sp, context);
        manager.AddNoteTargetsForTest(new PhysicalPoint(1600, 1000));

        Check.ThatCode(() => manager.OnGazeMoved(new PhysicalPoint(100, 100), 0)).DoesNotThrow();
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
            var closeCenter = ButtonCentrePhysical(anchor, 0);

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
                var closeCenter = ButtonCentrePhysical(anchor, 0);

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

                // And the gate itself: the source healthy, the leave satisfied, ✕ fires.
                throwOnRead = false;
                for (long t = 2_700_000; t <= 3_500_000; t += 100_000)
                    manager.OnGazeMoved(closeCenter, t);
                Check.WithCustomMessage("a recovered source must not keep the gate shut")
                    .That(manager.ActiveNotes).IsEmpty();
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
