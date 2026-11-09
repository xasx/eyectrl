namespace EyeCtrl.App.Tests.Analysis;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;
using static EyeCtrl.App.Tests.Analysis.RingArmReplayTests;
using static EyeCtrl.App.Tests.Analysis.RingPinModeReplayTests;

/// <summary>
/// The ring-arm look build's oracle (ticket ring-arm-look-build, task 4; harness from the replay's
/// author): the real VM, fed the product's message stream under each arm's Core policy, must draw
/// exactly what the replay model draws — 0 differing samples — on the scripted chains always and on
/// the frozen recording when <c>EYECTRL_GAZE_RECORDING</c> points at a copy of it. The Core-level
/// check proves the RunOnly engine's Pin/Release events are exactly what the harness's
/// <c>Feed</c> synthesizes, and the end-to-end check wires the engine and the VM together the way
/// production does.
/// </summary>
public class RingArmLookOracleTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalRect ToolbarRect = new(914, 0, 2012, 280);

    private static Func<IMessenger, CursorCircleViewModel> VmFor(RingHoldPolicy hold, double radiusPx) =>
        messenger =>
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.FixationRadiusPixels = radiusPx;
            return new CursorCircleViewModel(settings, messenger) { RingHoldPolicy = hold };
        };

    private static readonly (RingArmReplayTests.ArmSpec Spec, RingHoldPolicy Hold)[] Arms =
    [
        (RingArmReplayTests.Today, RingHoldPolicy.None),
        (RingArmReplayTests.RunOnly, RingHoldPolicy.None),
        (RingArmReplayTests.Hybrid, RingHoldPolicy.HybridFreeze),
    ];

    private static IEnumerable<(string Label, List<GazePoint> Samples)> Chains()
    {
        yield return ("plain chain (visible swallowed settle)", ScriptedSamples(withButtonVisit: false));
        yield return ("dwell chain (fire, 2.33 s hide, reappear)", ScriptedSamples(withButtonVisit: true));
        yield return ("freeze chain (a following ring reaches the button)", ScriptedFromElsewhere());
        yield return ("slip chain, return (an unproven freeze slips inside the zone, back in the hit area)", ScriptedSlips(SlipKind.Return));
        yield return ("slip chain, grace (an unproven freeze slips inside the zone for 660 ms)", ScriptedSlips(SlipKind.Grace));
        yield return ("slip chain, fly-through (unproven exit beyond the zone)", ScriptedSlips(SlipKind.FlyThrough));
        foreach (var (name, samples) in GoingForItChains.All())
            yield return ($"go chain: {name}", samples);
        yield return ("go chain: a corridor stop, then the eye goes for the replacement offer's button",
            GoingForItChains.StopNearP(new PhysicalPoint(2100, 1250)));
    }

    [Fact]
    public void TheRealVm_DrawsExactlyWhatTheModelDraws_ForEveryArm_OnEveryScriptedChain()
    {
        foreach (var (label, samples) in Chains())
        {
            var trace = Record(samples, SessionEnd);
            foreach (var (spec, hold) in Arms)
            {
                var vm = PlayThroughVm(trace, spec, VmFor(hold, trace.Config.RadiusPx));
                Check.WithCustomMessage($"{label}, {spec.Name}: samples where the real VM and the model disagree")
                    .That(CountDifferences(vm, Play(trace, spec))).IsEqualTo(0);
            }
        }
    }

    // ── Outcomes through the real engine + VM (the oracle alone cannot tell a shared design flaw) ──

    private sealed record WiredRun(
        List<(int Sample, string Event)> Log, List<ShowActivationButtonMessage> Offers, PhysicalPoint[] Position, bool[] Pinned)
    {
        public IEnumerable<string> Events => Log.Select(l => l.Event);

        public int FirstSample(string ev, int skipBefore = 0) =>
            Log.Where(l => l.Sample >= skipBefore && l.Event == ev).Select(l => l.Sample).DefaultIfEmpty(-1).First();
    }

    /// <summary>Plays <paramref name="samples"/> through the real engine (RunOnly) and the real hybrid VM,
    /// wired as in production, and records the ring's events with the sample they happened on.</summary>
    private static WiredRun RunWired(IReadOnlyList<GazePoint> samples)
    {
        var messenger = new StrongReferenceMessenger();
        var vm = VmFor(RingHoldPolicy.HybridFreeze, SessionEnd.RadiusPx)(messenger);
        PhysicalPoint drawn = default;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CursorCircleViewModel.Position)) drawn = vm.Position;
            else if (e.PropertyName == nameof(CursorCircleViewModel.MoveTo)) drawn = vm.MoveTo.Target;
        };

        int current = 0;
        var log = new List<(int, string)>();
        var offers = new List<ShowActivationButtonMessage>();
        messenger.Register<ShowActivationButtonMessage>(offers, (_, m) =>
        {
            offers.Add(m);
            log.Add((current, "offer"));
        });
        vm.RingStayStarted += _ => log.Add((current, "stay start"));
        vm.RingStayEnded += e => log.Add((current, $"stay end {e.Reason}"));
        vm.RingHoldStarted += e => log.Add((current, $"hold start {e.Kind}"));
        vm.RingHoldEnded += e => log.Add((current, $"hold end {e.Reason}"));
        vm.RingReleased += e => log.Add((current, $"release {e.Cause}"));
        vm.RingPinProcessed += e => log.Add((current, e.Applied ? "pin" : e.IgnoredNearOffer ? "pin near-miss" : "pin refused"));

        var position = new PhysicalPoint[samples.Count];
        var pinned = new bool[samples.Count];
        Walk(samples, SessionEnd, RingPinPolicy.RunOnly, messenger: messenger, afterSample: i =>
        {
            position[i] = drawn;
            pinned[i] = vm.IsPinned;
            current = i + 1;
        });
        return new WiredRun(log, offers, position, pinned);
    }

    [Fact]
    public void Outcome_AfterACorridorStop_TheReplacementKeepsThePin_AndItsOwnApproachGetsAStay()
    {
        // The corridor stop's settle (P's side, inside the stay zone) re-pins during the stay; the engine then
        // replaces the offer while that settle's run still holds. The Hide ends the stay WITHOUT a release
        // (Core's RunOnly pin is live), so the replacement comes up with a PINNED ring at the new P, and the
        // next look toward the new button gets a stay of its own. Mutant: the Hide always releases.
        var first = RunWired(GoingForItChains.StopNearP(null));
        var events = first.Events.ToList();

        Check.WithCustomMessage("the corridor stop's settle replaces the offer: " + string.Join(" | ", events))
            .That(first.Offers.Count).IsGreaterOrEqualThan(2);
        int closed = events.IndexOf("stay end OfferClosed");
        Check.WithCustomMessage(string.Join(" | ", events)).That(closed).IsGreaterOrEqualThan(0);
        Check.That(events[closed + 1]).IsEqualTo("offer");          // the Show follows; no release in between
        int replaced = first.Log.Where(l => l.Event == "offer").Skip(1).First().Sample;
        Check.WithCustomMessage($"the replacement offer is up at sample {replaced}: the ring must be pinned there")
            .That(first.Pinned[replaced]).IsTrue();
        var newPoint = first.Offers[1].OfferPoint!.Value;
        Check.WithCustomMessage("the ring rests at the new P while the stop lasts")
            .That(Enumerable.Range(replaced, 14).Any(i => first.Pinned[i] && first.Position[i].DistanceTo(newPoint) < 1.0)).IsTrue();

        // The eye now goes for the NEW button: its run end starts a stay keyed to the new offer, and the ring
        // stays at the new P until that stay ends.
        var second = RunWired(GoingForItChains.StopNearP(first.Offers[1].ButtonCenter));
        var all = second.Events.ToList();
        int secondOffer = all.IndexOf("offer", all.IndexOf("offer") + 1);
        Check.WithCustomMessage("the second approach: " + string.Join(" | ", all)).That(secondOffer).IsGreaterOrEqualThan(0);
        int secondOfferSample = second.Log.Where(l => l.Event == "offer").Skip(1).First().Sample;
        int stayStart = second.FirstSample("stay start", secondOfferSample + 1);
        Check.WithCustomMessage("the second approach: " + string.Join(" | ", all)).That(stayStart).IsGreaterOrEqualThan(0);
        int stayEnd = second.Log.Where(l => l.Sample >= stayStart && l.Event.StartsWith("stay end", StringComparison.Ordinal))
            .Select(l => l.Sample).First();
        var p2 = second.Offers[1].OfferPoint!.Value;
        Check.That(stayEnd - stayStart).IsStrictlyGreaterThan(10);
        for (int i = stayStart; i < stayEnd; i++)
        {
            Check.WithCustomMessage($"sample {i}: the ring stays at the new P while the eye goes for the new button")
                .That(second.Pinned[i] && second.Position[i].DistanceTo(p2) < 1.0).IsTrue();
        }
    }

    [Fact]
    public void Outcome_DriftEndedRun_GoingForTheButton_StaysStillAtP_ThroughTheButtonVisit()
    {
        // F3, now with the stay: the run ends through fixation drift, 66 px from P (outside B's zone, inside
        // the corridor): the stay starts, the eye reaches the button and dwells, and the ring never moves
        // and never freezes elsewhere until the stay ends (the fire closes the offer).
        var run = RunWired(GoingForItChains.Drift(GoingForItChains.DriftKind.ToButton));
        var events = run.Events.ToList();
        int start = events.IndexOf("stay start");
        Check.WithCustomMessage(string.Join(" | ", events)).That(start).IsGreaterOrEqualThan(0);
        int end = events.FindIndex(start, e => e.StartsWith("stay end", StringComparison.Ordinal));
        Check.WithCustomMessage(string.Join(" | ", events)).That(end).IsStrictlyGreaterThan(start);
        // The fire ends the stay: the walk completes the activation synchronously, so the manual selection's
        // hiding state can reach the ring before the offer's Hide (the adapter posts the Hide first).
        Check.WithCustomMessage(string.Join(" | ", run.Log.Select(l => $"{l.Sample}:{l.Event}")))
            .That(events[end]).IsOneOf("stay end OfferClosed", "stay end Hidden");
        Check.That(events.Skip(start).Take(end - start)).Not.Contains("hold start Freeze");

        int startSample = run.Log.First(l => l.Event == "stay start").Sample;
        int endSample = run.Log.First(l => l.Event == events[end]).Sample;
        for (int i = startSample; i < endSample; i++)
        {
            Check.WithCustomMessage($"sample {i}: the ring stays pinned and still while the eye is at the button")
                .That(run.Pinned[i]).IsTrue();
            Check.That(run.Position[i]).IsEqualTo(run.Position[startSample]);
        }
    }

    [Fact]
    public void Outcome_ANearMissSettleDuringTheStay_IsRefused_AndTheRingStaysAtP()
    {
        // Review-02 G1, turned around by the stay: the eye resumes at the near-miss spot (240 px from B: in the
        // zone, outside the 200-px hit) after a gap, the stay starts, and the settle there is refused (D). The
        // stay goes on: the ring stays at P, with no release, until the eye leaves for the far hold.
        var run = RunWired(GoingForItChains.GapToNearMiss());
        var events = run.Events.ToList();
        int start = events.IndexOf("stay start");
        int nearMiss = events.IndexOf("pin near-miss");
        Check.WithCustomMessage(string.Join(" | ", events)).That(start).IsGreaterOrEqualThan(0);
        Check.WithCustomMessage(string.Join(" | ", events)).That(nearMiss).IsStrictlyGreaterThan(start);
        Check.That(events.Skip(start).Take(nearMiss - start + 1).Where(e => e.StartsWith("stay end", StringComparison.Ordinal)
            || e.StartsWith("release", StringComparison.Ordinal))).IsEmpty();

        int startSample = run.FirstSample("stay start");
        int nearMissSample = run.FirstSample("pin near-miss");
        for (int i = startSample; i <= nearMissSample + 3; i++)
        {
            Check.WithCustomMessage($"sample {i}: the ring stays at P across the refused near-miss settle")
                .That(run.Pinned[i] && run.Position[i] == run.Position[startSample]).IsTrue();
        }
    }

    [Fact]
    public void Outcome_DriftEndedRun_LookingAway_FollowsAfterTheGrace()
    {
        // F3, the other way: the same drift-ended run, then the eye looks 500 px to the side. Whatever the
        // landing (a stay if the run ended in the corridor), the ring is released no later than the grace
        // after the eye left the zone, and follows.
        var run = RunWired(GoingForItChains.Drift(GoingForItChains.DriftKind.Away));
        var events = run.Events.ToList();
        Check.WithCustomMessage(string.Join(" | ", events)).That(events).Contains("release RunEnd");
        int released = run.FirstSample("release RunEnd");
        Check.That(run.Pinned[released]).IsFalse();
        int stay = run.FirstSample("stay start");
        if (stay >= 0)
        {
            Check.That(events).Contains("stay end Left");
            // The eye leaves for the side in 5 samples (150 ms), so the release comes within 500 ms + 150 ms.
            Check.That(released - stay).IsLessOrEqualThan((500 + 150) / 30 + 1);
        }
    }

    [Fact]
    public void Core_RunOnlyEvents_AreExactlyWhatFeedSynthesizes()
    {
        // The engine with RingPinPolicy.RunOnly must raise PinRing on every accepted settle (swallowed
        // included — the swallowed settle is a Kind.Settled of the recorded trace) and ReleaseRing on
        // every exit from Fixation, in the samples and places the harness's Feed replays. Mutation:
        // pin from the offer's publish / miss a settle → the sequences diverge.
        foreach (var (label, samples) in Chains())
        {
            var trace = Record(samples, SessionEnd);
            var expected = SynthesizedRingEvents(trace);
            var actual = EngineRingEvents(samples, SessionEnd, RingPinPolicy.RunOnly);

            Check.WithCustomMessage($"{label}: the RunOnly engine's ring events vs what Feed synthesizes")
                .That(actual).ContainsExactly(expected);
        }
    }

    [Fact]
    public void TheWiredEngineAndTheHybridVm_DrawExactlyWhatTheModelDraws()
    {
        // End to end, as production wires it: the engine runs RunOnly (its PinRing/ReleaseRing fire at
        // the settles and the exits) and the real VM holds with HybridFreeze; every message is
        // forwarded synchronously, in the engine's call order — the adapter's order. Mutation: any
        // deviation in the hold's rules → differing samples.
        foreach (var (label, samples) in Chains())
        {
            var trace = Record(samples, SessionEnd);

            var messenger = new StrongReferenceMessenger();
            var vm = VmFor(RingHoldPolicy.HybridFreeze, trace.Config.RadiusPx)(messenger);
            PhysicalPoint drawn = default;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CursorCircleViewModel.Position)) drawn = vm.Position;
                else if (e.PropertyName == nameof(CursorCircleViewModel.MoveTo)) drawn = vm.MoveTo.Target;
            };

            var n = samples.Count;
            var drawnPerSample = new PhysicalPoint[n];
            var visible = new bool[n];
            var pinned = new bool[n];

            Walk(samples, SessionEnd, RingPinPolicy.RunOnly,
                messenger: messenger,
                afterSample: i =>
                {
                    drawnPerSample[i] = drawn;
                    visible[i] = vm.IsVisible;
                    pinned[i] = vm.IsPinned;
                });

            var wired = new ArmTrace("wired", drawnPerSample, visible, pinned);
            Check.WithCustomMessage($"{label}: samples where the wired engine+VM and the model disagree")
                .That(CountDifferences(wired, Play(trace, Hybrid))).IsEqualTo(0);
        }
    }

    [Fact]
    public void TheRecording_RealVmMatchesTheModel_ForEveryArm_WhenTheRecordingIsProvided()
    {
        var path = Environment.GetEnvironmentVariable("EYECTRL_GAZE_RECORDING");
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "No gaze recording. Set EYECTRL_GAZE_RECORDING to the frozen snapshot copy.");

        var samples = ReadRecording(path!);
        foreach (var config in new[] { SessionEnd, SessionStart })
        {
            var trace = Record(samples, config);
            CheckEquivalent(trace);   // the harness's own oracle: model ≡ the phase-2 real VMs
            foreach (var (spec, hold) in Arms)
            {
                var vm = PlayThroughVm(trace, spec, VmFor(hold, trace.Config.RadiusPx));
                Check.WithCustomMessage($"{config.Label}, {spec.Name}: samples where the real VM and the model disagree")
                    .That(CountDifferences(vm, Play(trace, spec))).IsEqualTo(0);
            }
        }
    }

    // ── The helpers the two walks share ──────────────────────────────────────

    /// <summary>What the harness's <c>Feed</c> replays for the RunOnly policy, derived from the
    /// recorded trace: a pin at every accepted settle, a release on every exit from Fixation.</summary>
    private static List<(int Sample, string Kind, PhysicalPoint Point)> SynthesizedRingEvents(
        RingArmReplayTests.Trace trace)
    {
        var events = new List<(int, string, PhysicalPoint)>();
        var previous = InteractionState.FreeLook;
        for (int i = 0; i < trace.Samples.Count; i++)
        {
            var row = trace.Samples[i];
            for (int k = row.FirstEvent; k < row.FirstEvent + row.EventCount; k++)
            {
                var e = trace.Events[k];
                if (e.Kind == RingArmReplayTests.Kind.Settled)
                    events.Add((i, "pin", e.Point));
                else if (e.Kind == RingArmReplayTests.Kind.State)
                {
                    if (previous == InteractionState.Fixation && e.State != InteractionState.Fixation)
                        events.Add((i, "release", default));
                    previous = e.State;
                }
            }
        }

        return events;
    }

    /// <summary>The real engine's ring events under <paramref name="policy"/>, per sample.</summary>
    private static List<(int Sample, string Kind, PhysicalPoint Point)> EngineRingEvents(
        IReadOnlyList<GazePoint> samples, RingPinModeReplayTests.Config config, RingPinPolicy policy)
    {
        var events = new List<(int, string, PhysicalPoint)>();
        int current = 0;
        Walk(samples, config, policy,
            onPin: p => events.Add((current, "pin", p)),
            onRelease: () => events.Add((current, "release", default)),
            afterSample: i => current = i + 1);
        return events;
    }

    /// <summary>
    /// One engine walk, byte-identical to the harness's <c>Record</c> body (settings, chrome zones,
    /// completion handling), with the ring policy chosen and messages forwarded when
    /// <paramref name="messenger"/> is given — the adapter's synchronous-forwarding stand-in.
    /// </summary>
    private static void Walk(
        IReadOnlyList<GazePoint> samples,
        RingPinModeReplayTests.Config config,
        RingPinPolicy policy,
        IMessenger? messenger = null,
        Action<int>? afterSample = null,
        Action<PhysicalPoint>? onPin = null,
        Action? onRelease = null)
    {
        var settings = new EyeCtrlSettings();
        // Pinned at the settle (500 = no show delay): round button B3's delay has its own tests.
        settings.Dwell.OfferShowAfterMs = 500;
        settings.Dwell.FixationRadiusPixels = config.RadiusPx;
        settings.Dwell.FixationDetectionMs = config.OnsetMs;
        settings.Dwell.ActivationButtonDwellMs = config.ButtonDwellMs;
        settings.Cursor.CircleSize = config.RingSizeDip;
        settings.SelectionMode = SelectionMode.ManualSelection;

        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);
        var im = engine.InteractionManager;
        im.SelectionMode = SelectionMode.ManualSelection;
        im.RingPinPolicy = policy;

        im.PinRing += p => { onPin?.Invoke(p); messenger?.Send(new PinRingMessage(p)); };
        im.ReleaseRing += () => { onRelease?.Invoke(); messenger?.Send(new ReleaseRingMessage()); };
        im.StateChanged += s => messenger?.Send(new StateChangedMessage(s));
        im.ActivationOfferShown += o =>
            messenger?.Send(new ShowActivationButtonMessage(o.Button, o.HitRadiusPx, o.Point, engine.EffectiveFixationRadiusPixels));
        im.ActivationOfferClosed += () => messenger?.Send(new HideActivationButtonMessage());
        ActivationOffer? pendingFire = null;
        im.ActivationOfferFired += (o, _) => pendingFire = o;
        engine.CursorMoved += frame => messenger?.Send(new GazeCursorMovedMessage(frame));

        var visibility = new ToolbarVisibilityStub();
        var autoHide = new ToolbarAutoHideController(
            enabled: true, edge: "Top", screenW: Screen.Width, screenH: Screen.Height,
            toolbarRect: ToolbarRect, hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
        autoHide.Attach(visibility);
        bool? lastVisible = null;
        long? closeManualSelectionAtUs = null;

        engine.Start();
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            long us = sample.TimestampMicroseconds;
            autoHide.Feed(sample, us);
            if (lastVisible != visibility.Visible)
            {
                lastVisible = visibility.Visible;
                im.SetActivationChromeZones(visibility.Visible ? new[] { ToolbarRect } : Array.Empty<PhysicalRect>());
            }

            provider.Raise(sample);

            if (pendingFire is { } fired)
            {
                pendingFire = null;
                if (im.OnActivationButtonDwelled(fired) == ActivationCompletionResult.Accepted)
                    closeManualSelectionAtUs = us + ManualSelectionVisitUs;
            }
            if (closeManualSelectionAtUs is { } closeAt && us >= closeAt)
            {
                closeManualSelectionAtUs = null;
                im.OnManualSelectionBack();
            }

            afterSample?.Invoke(i);
        }
    }

    private sealed class ToolbarVisibilityStub : IToolbarVisibility
    {
        public bool Visible { get; private set; } = true;
        public void ShowToolbar() => Visible = true;
        public void HideToolbar(bool paused) => Visible = false;
        public void ForceShow() => Visible = true;
        public void SetPauseIndicator(bool visible) { }
        public void PositionIndicator(string edge) { }
        public void SetActive(bool active) { }
    }
}
