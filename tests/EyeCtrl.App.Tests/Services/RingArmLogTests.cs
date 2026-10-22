namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Startup;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The ring-arm look build's log lines (ticket ring-arm-look-build, "Log lines (exact; invariant
/// culture; physical px)"). The startup line is always written; the event lines exist only when
/// <c>--ring-arm</c> was given, and the session scripts parse them — every format is pinned here.
/// </summary>
public class RingArmLogTests
{
    // ── The startup line: always written, one per launch ─────────────────────

    [Fact]
    public void StartupLine_Default_RunsHybrid_PrintsTheHybridParameters_AndTheDefaultSource()
    {
        // Since the owner's look, a launch with no --ring-arm runs the hybrid (B); the line names it
        // so the readout can see which arm actually ran.
        var launch = RingArmArgument.Parse(new[] { "EyeCtrl.exe" });

        Check.That(RingArmLog.StartupLine(launch)).IsEqualTo(
            "Ring arm: hybrid (pin=RunOnly, hold=HybridFreeze, grace=500 ms once proven 90 ms, "
            + "go=500 ms corridor=R, glideOut=0) [source: default]");
    }

    [Fact]
    public void StartupLine_Hybrid_PrintsTheHybridParameters_AndTheArgumentSource()
    {
        var launch = RingArmArgument.Parse(new[] { "--ring-arm=hybrid" });

        Check.That(RingArmLog.StartupLine(launch)).IsEqualTo(
            "Ring arm: hybrid (pin=RunOnly, hold=HybridFreeze, grace=500 ms once proven 90 ms, "
            + "go=500 ms corridor=R, glideOut=0) [source: --ring-arm]");
    }

    [Fact]
    public void StartupLine_RunOnly_PrintsZeroHoldParameters_AndTheArgumentSource()
    {
        var launch = RingArmArgument.Parse(new[] { "--ring-arm=run-only" });

        Check.That(RingArmLog.StartupLine(launch)).IsEqualTo(
            "Ring arm: run-only (pin=RunOnly, hold=None, grace=0 ms once proven 0 ms, "
            + "go=0 ms corridor=R, glideOut=0) [source: --ring-arm]");
    }

    [Fact]
    public void StartupLine_Today_Diagnostic_PrintsZeroHoldParameters_AndTheArgumentSource()
    {
        var launch = RingArmArgument.Parse(new[] { "--ring-arm=today" });

        Check.That(RingArmLog.StartupLine(launch)).IsEqualTo(
            "Ring arm: today (pin=OfferFollowing, hold=None, grace=0 ms once proven 0 ms, "
            + "go=0 ms corridor=R, glideOut=0) [source: --ring-arm]");
    }

    [Fact]
    public void StartupLine_UnknownValue_FallsBackToTheDefault_ButKeepsTheArgumentSource()
    {
        // An unknown value falls back to the default (hybrid), and the source stays --ring-arm so
        // the fallback is visible in the readout.
        var launch = RingArmArgument.Parse(new[] { "--ring-arm=fast" });

        Check.That(RingArmLog.StartupLine(launch)).IsEqualTo(
            "Ring arm: hybrid (pin=RunOnly, hold=HybridFreeze, grace=500 ms once proven 90 ms, "
            + "go=500 ms corridor=R, glideOut=0) [source: --ring-arm]");
    }

    [Fact]
    public void UnknownArmLine_HasTheExactFormatTheReadoutMatches()
    {
        // The look's readout matches `Ring arm value '(?<value>[^']*)' is unknown`
        // (ringlook-lib.ps1); the text is a contract (the tail says the fallback's arm — hybrid now).
        // Mutant X15 (deleting the warning call) reds the wiring test; this one reds if the text
        // moves away from the pattern.
        Check.That(RingArmLog.UnknownArmLine("fast")).IsEqualTo(
            "Ring arm value 'fast' is unknown (today|hybrid|run-only) — running hybrid.");
    }

    // ── The event lines: exact formats ───────────────────────────────────────

    [Fact]
    public void EventLines_HaveTheExactFormatsTheSessionScriptsParse()
    {
        Check.That(RingArmLog.PinLine(atOffer: true, new PhysicalPoint(1000, 1000), 287.4))
            .IsEqualTo("Ring pin (offer) at=(1000,1000) dist=287");
        Check.That(RingArmLog.PinLine(atOffer: false, new PhysicalPoint(1234.6, 998.4), 0))
            .IsEqualTo("Ring pin (settle) at=(1235,998) dist=0");

        Check.That(RingArmLog.IgnoredPinLine(atOffer: false, new PhysicalPoint(1234.6, 998.4)))
            .IsEqualTo("Ring pin (settle) at=(1235,998) ignored=near-offer");

        Check.That(RingArmLog.GoStartLine(40.4, 3.6)).IsEqualTo("Ring go start dist=40 off=4");
        Check.That(RingArmLog.GoStartLine(40.4, null)).IsEqualTo("Ring go start dist=40 off=na");
        Check.That(RingArmLog.GoEndLine(RingGoEndReason.Hit, 241.4, 12.2)).IsEqualTo("Ring go end (hit) ms=241 off=12");
        Check.That(RingArmLog.GoEndLine(RingGoEndReason.Left, 90, 100)).IsEqualTo("Ring go end (left) ms=90 off=100");
        Check.That(RingArmLog.GoEndLine(RingGoEndReason.Timeout, 510, 5)).IsEqualTo("Ring go end (timeout) ms=510 off=5");
        Check.That(RingArmLog.GoEndLine(RingGoEndReason.OfferClosed, 30, 0)).IsEqualTo("Ring go end (offer-closed) ms=30 off=0");
        Check.That(RingArmLog.GoEndLine(RingGoEndReason.Hidden, 30, null)).IsEqualTo("Ring go end (hidden) ms=30 off=na");

        Check.That(RingArmLog.ReleaseLine(RingReleaseCause.RunEnd, 123.6))
            .IsEqualTo("Ring release (run-end) dist=124");
        Check.That(RingArmLog.ReleaseLine(RingReleaseCause.OfferClosed, 7.2))
            .IsEqualTo("Ring release (offer-closed) dist=7");
        Check.That(RingArmLog.ReleaseLine(RingReleaseCause.HoldEnd, 664))
            .IsEqualTo("Ring release (hold-end) dist=664");
        Check.That(RingArmLog.ReleaseLine(RingReleaseCause.HidingState, 12.5))
            .IsEqualTo("Ring release (state) dist=13");

        Check.That(RingArmLog.HoldStartLine(RingHoldStartKind.Keep)).IsEqualTo("Ring hold start (keep)");
        Check.That(RingArmLog.HoldStartLine(RingHoldStartKind.Approach)).IsEqualTo("Ring hold start (approach)");
        Check.That(RingArmLog.HoldStartLine(RingHoldStartKind.Freeze)).IsEqualTo("Ring hold start (freeze)");

        Check.That(RingArmLog.HoldEndLine(RingHoldEndReason.Left, 967.4))
            .IsEqualTo("Ring hold end (left) heldMs=967");
        Check.That(RingArmLog.HoldEndLine(RingHoldEndReason.Unproven, 30))
            .IsEqualTo("Ring hold end (unproven) heldMs=30");
        Check.That(RingArmLog.HoldEndLine(RingHoldEndReason.OfferClosed, 500))
            .IsEqualTo("Ring hold end (offer-closed) heldMs=500");
        Check.That(RingArmLog.HoldEndLine(RingHoldEndReason.Hidden, 42.2))
            .IsEqualTo("Ring hold end (hidden) heldMs=42");
    }

    // ── The wiring: Core event -> VM record -> line ──────────────────────────

    [Fact]
    public void TheLog_WritesASettlePinAndARunEndRelease_FromTheMessageFlow()
    {
        var log = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(new EyeCtrlSettings(), messenger);
        var im = new InteractionManager();
        using var sink = new RingArmLog(im, vm, log.Add);

        // The adapter's forwarding, as production wires it.
        im.PinRing += p => messenger.Send(new PinRingMessage(p));
        im.ReleaseRing += () => messenger.Send(new ReleaseRingMessage());
        im.StateChanged += s => messenger.Send(new StateChangedMessage(s));

        var p = new PhysicalPoint(1000, 1000);
        im.OnFixationStarted(p);
        messenger.Send(new GazeCursorMovedMessage(p, 30_000));
        im.OnFixationSettled(p);                          // no engine: a settle pin, atOffer false
        im.OnFixationEnded();                             // no offer: the release is the run end

        Check.That(log).HasSize(2);
        Check.That(log[0]).IsEqualTo("Ring pin (settle) at=(1000,1000) dist=0");
        Check.That(log[1].StartsWith("Ring release (run-end) dist=", StringComparison.Ordinal)).IsTrue();
    }

    [Fact]
    public void TheLog_WritesTheIgnoredNearOfferLine_OnlyForTheNearMissRefusal()
    {
        // GL1: a near-miss settle the ring ignores (D) logs `Ring pin (settle) at=(x,y)
        // ignored=near-offer`. Every other refusal stays silent: a pin on P's side of the zone applies
        // and logs the normal line; a pin refused only because no fixation is up writes nothing.
        // Mutant ML1: drop the reason from the VM's event -> no line.
        var log = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(new EyeCtrlSettings(), messenger)
        {
            RingHoldPolicy = RingHoldPolicy.HybridFreeze,
        };
        var im = new InteractionManager();
        using var sink = new RingArmLog(im, vm, log.Add);
        im.PinRing += p => messenger.Send(new PinRingMessage(p));
        im.StateChanged += s => messenger.Send(new StateChangedMessage(s));

        var offerPoint = new PhysicalPoint(300, 300);
        var button = new PhysicalPoint(600, 600);
        messenger.Send(new GazeCursorMovedMessage(offerPoint, 30_000));
        messenger.Send(new ShowActivationButtonMessage(button, 150, offerPoint, 80));

        // No fixation up: a near-miss pin is refused by the state rule, not by D: silent.
        var nearMiss = new PhysicalPoint(800, 600);                // 200 px from B, 583 from P
        messenger.Send(new PinRingMessage(nearMiss));
        Check.That(log).IsEmpty();

        im.OnFixationStarted(nearMiss);                            // the state reaches the VM: Fixation
        im.OnFixationSettled(nearMiss);                            // Core's settle pin: D ignores it

        Check.That(log).ContainsExactly("Ring pin (settle) at=(800,600) ignored=near-offer");

        // A settle on P's side of the zone (225 px from B, 199 px from P) applies as before.
        im.OnFixationEnded();
        log.Clear();
        var swallowed = new PhysicalPoint(441, 441);
        im.OnFixationStarted(swallowed);
        im.OnFixationSettled(swallowed);

        Check.That(log).HasSize(1);
        Check.That(log[0].StartsWith("Ring pin (settle) at=(441,441) dist=", StringComparison.Ordinal)).IsTrue();
    }

    [Fact]
    public void TheLog_AHoldBeatsTheNearOfferReason_NoIgnoredLineDuringAHold()
    {
        // R3: a near-miss settle pin that arrives while the ring is holding is refused by the hold, not
        // by D, so the log stays silent and the ring stays held. Mutant ML2: drop `!holdSuppressed`
        // from the VM's reason -> the ignored=near-offer line appears.
        var log = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(new EyeCtrlSettings(), messenger)
        {
            RingHoldPolicy = RingHoldPolicy.HybridFreeze,
        };
        var im = new InteractionManager();
        using var sink = new RingArmLog(im, vm, log.Add);
        im.PinRing += p => messenger.Send(new PinRingMessage(p));
        im.StateChanged += s => messenger.Send(new StateChangedMessage(s));

        var offerPoint = new PhysicalPoint(300, 300);
        var button = new PhysicalPoint(600, 600);
        messenger.Send(new GazeCursorMovedMessage(offerPoint, 30_000));
        messenger.Send(new ShowActivationButtonMessage(button, 150, offerPoint, 80));
        messenger.Send(new GazeCursorMovedMessage(button, 60_000));   // into the hit area: the freeze starts
        Check.That(vm.IsPinned).IsTrue();
        log.Clear();                                                   // the hold-start line is not under test

        var nearMiss = new PhysicalPoint(800, 600);                    // 200 px from B, 583 from P
        im.OnFixationStarted(nearMiss);                                // the state reaches the VM: Fixation
        im.OnFixationSettled(nearMiss);                                // Core's settle pin: the hold refuses it

        Check.WithCustomMessage("logged: " + string.Join(" | ", log))
            .That(log.Any(line => line.Contains("ignored=", StringComparison.Ordinal))).IsFalse();
        Check.That(vm.IsPinned).IsTrue();                              // still held, where it froze
    }

    [Fact]
    public void TheLog_WritesTheGoLines_ForEachWayTheKeepEnds()
    {
        // The going-for-it keep's lines, through the real VM: start (ring-to-gaze distance, the gaze's
        // distance to the P-B segment), and one end line per reason with the time since the start.
        // P = (1000,1000), B = (1400,1000), hit 150, R 80, frames 30 ms. Mutant M10: the go-end reasons
        // swapped (left <-> timeout) -> the timeout and left lines read wrongly.
        List<string> Go(Action<Action<PhysicalPoint>, Action<InteractionState>, Action> script)
        {
            var log = new List<string>();
            var messenger = new WeakReferenceMessenger();
            var vm = new CursorCircleViewModel(new EyeCtrlSettings(), messenger)
            {
                RingHoldPolicy = RingHoldPolicy.HybridFreeze,
            };
            var im = new InteractionManager();
            using var sink = new RingArmLog(im, vm, log.Add);
            long us = 0;
            void Frame(PhysicalPoint at) => messenger.Send(new GazeCursorMovedMessage(at, us += 30_000));
            var p = new PhysicalPoint(1000, 1000);
            Frame(p);
            messenger.Send(new StateChangedMessage(InteractionState.Fixation));
            messenger.Send(new PinRingMessage(p));
            for (int i = 0; i < 10; i++) Frame(p);
            messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(1400, 1000), 150, p, 80));
            messenger.Send(new ReleaseRingMessage());
            messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
            log.Clear();
            script(Frame, s => messenger.Send(new StateChangedMessage(s)), () => messenger.Send(new HideActivationButtonMessage()));
            return log.Where(l => l.StartsWith("Ring go ", StringComparison.Ordinal)).ToList();
        }

        Check.That(Go((frame, _, _) =>
        {
            frame(new PhysicalPoint(1100, 1000));
            for (int i = 0; i < 17; i++) frame(new PhysicalPoint(1100, 1000));
        })).ContainsExactly("Ring go start dist=100 off=0", "Ring go end (timeout) ms=510 off=0");

        Check.That(Go((frame, _, _) =>
        {
            frame(new PhysicalPoint(1100, 1000));
            frame(new PhysicalPoint(1100, 1100));
        })).ContainsExactly("Ring go start dist=100 off=0", "Ring go end (left) ms=30 off=100");

        Check.That(Go((frame, _, _) =>
        {
            frame(new PhysicalPoint(1100, 1000));
            frame(new PhysicalPoint(1300, 1000));
        })).ContainsExactly("Ring go start dist=100 off=0", "Ring go end (hit) ms=30 off=0");

        Check.That(Go((frame, _, hide) =>
        {
            frame(new PhysicalPoint(1100, 1000));
            frame(new PhysicalPoint(1100, 1000));
            hide();
        })).ContainsExactly("Ring go start dist=100 off=0", "Ring go end (offer-closed) ms=30 off=0");

        Check.That(Go((frame, state, _) =>
        {
            frame(new PhysicalPoint(1100, 1000));
            frame(new PhysicalPoint(1100, 1000));
            state(InteractionState.Paused);
        })).ContainsExactly("Ring go start dist=100 off=0", "Ring go end (hidden) ms=30 off=0");
    }

    [Fact]
    public void TheLog_ReportsAnOfferPin_FromTheWiredEngine()
    {
        // The origin is Core's truth (RingPinnedAtOffer), snapshotted where the event is raised: with
        // a real engine the offer's appearance is an OFFER pin.
        var settings = new EyeCtrlSettings();
        settings.Dwell.FixationRadiusPixels = 90;
        settings.Dwell.FixationDetectionMs = 300;
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(new PhysicalRect(0, 0, 3840, 2400), scale: 2.0);
        var im = engine.InteractionManager;

        var log = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);
        using var sink = new RingArmLog(im, vm, log.Add);
        im.PinRing += p => messenger.Send(new PinRingMessage(p));
        im.StateChanged += s => messenger.Send(new StateChangedMessage(s));
        im.ActivationOfferShown += o =>
            messenger.Send(new ShowActivationButtonMessage(o.Button, o.HitRadiusPx));

        long us = 0;
        engine.Start();
        for (int i = 0; i < 20; i++)
        {
            us += 30_000;
            provider.Raise(new GazePoint(1000, 1000, us, true));
        }

        Check.WithCustomMessage("the created offer's pin must be labelled offer; logged: "
                + string.Join(" | ", log))
            .That(log.Any(line => line.StartsWith("Ring pin (offer) ", StringComparison.Ordinal))).IsTrue();
        Check.That(log.Any(line => line.StartsWith("Ring pin (settle) ", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void TheOriginFifo_DequeuesOneTicketPerProcessedPin_AppliedOrRefused()
    {
        // Review 01's X10: a refused pin must still consume its origin ticket, or the next applied
        // pin drains the wrong one and mislabels. Refused here: the offer's creation pin arrives
        // while the VM has not seen its onset (its last state is FreeLook); the applied pin after it
        // is the chrome settle. Mutant: dequeue only on applied pins → the settle pin logs as offer.
        var settings = new EyeCtrlSettings();
        settings.Dwell.FixationRadiusPixels = 90;
        settings.Dwell.FixationDetectionMs = 300;
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(new PhysicalRect(0, 0, 3840, 2400), scale: 2.0);
        var im = engine.InteractionManager;

        var log = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);
        using var sink = new RingArmLog(im, vm, log.Add);
        im.PinRing += p => messenger.Send(new PinRingMessage(p));
        im.ActivationOfferShown += o =>
            messenger.Send(new ShowActivationButtonMessage(o.Button, o.HitRadiusPx));
        engine.CursorMoved += frame => messenger.Send(new GazeCursorMovedMessage(frame));

        bool droppedOnset = false;
        im.StateChanged += s =>
        {
            if (!droppedOnset && s == InteractionState.Fixation)
            {
                droppedOnset = true;
                return;                                            // the VM stays FreeLook: the pin is refused
            }
            messenger.Send(new StateChangedMessage(s));
        };

        long us = 0;
        void Feed(PhysicalPoint p, int samples)
        {
            for (int i = 0; i < samples; i++)
            {
                us += 30_000;
                provider.Raise(new GazePoint(p.X, p.Y, us, true));
            }
        }
        void Saccades(PhysicalPoint a, PhysicalPoint b, int samples)
        {
            for (int i = 0; i < samples; i++)
            {
                us += 30_000;
                var p = i % 2 == 0 ? a : b;
                provider.Raise(new GazePoint(p.X, p.Y, us, true));
            }
        }

        var settlePoint = new PhysicalPoint(2400, 1400);
        engine.Start();
        Feed(new PhysicalPoint(1000, 1000), 20);                   // the creation's offer pin: refused
        Saccades(new PhysicalPoint(3000, 2000), new PhysicalPoint(500, 300), 20);   // the run ends

        im.SetActivationChromeZones(new PhysicalRect(2000, 1000, 800, 800));
        Feed(settlePoint, 20);                                     // a chrome settle: a settle pin, applied

        Check.WithCustomMessage("logged: " + string.Join(" | ", log))
            .That(log).Contains("Ring pin (settle) at=(2400,1400) dist=0");
        Check.WithCustomMessage("logged: " + string.Join(" | ", log))
            .That(log.Any(line => line.StartsWith("Ring pin (offer) ", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void Dispose_Unsubscribes_TheLogGoesSilent()
    {
        var log = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(new EyeCtrlSettings(), messenger);
        var im = new InteractionManager();
        var sink = new RingArmLog(im, vm, log.Add);
        im.PinRing += p => messenger.Send(new PinRingMessage(p));
        im.StateChanged += s => messenger.Send(new StateChangedMessage(s));

        var p = new PhysicalPoint(1000, 1000);
        im.OnFixationStarted(p);
        im.OnFixationSettled(p);
        Check.That(log).HasSize(1);

        sink.Dispose();

        im.OnFixationSettled(new PhysicalPoint(1001, 1000));   // Core still pins; the log is gone
        Check.That(log).HasSize(1);
    }
}
