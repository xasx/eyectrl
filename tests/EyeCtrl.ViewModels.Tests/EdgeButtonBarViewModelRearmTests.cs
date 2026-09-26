namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

public class EdgeButtonBarViewModelRearmTests
{
    // The DETECTOR MODEL (round-1 review, F9): the first inside sample only SETS
    // EnteredTimestamp (DwellDetector.cs:363–367); a LATER sample with elapsed ≥ dwell fires
    // (:368–385). Every timeline below uses two inside samples to toggle. D = the debounce,
    // 150 ms = 150_000 µs — the MaxCreditedSampleGapUs precedent.
    private const long D = 150_000;
    private static readonly PhysicalPoint Anchor = new(500, 500);

    private static EdgeButtonDefinition Def(string id, List<string> toggles, double verticalOffset = 0,
        int dwellMs = 1, bool rearm = true, Action? afterToggle = null) => new(
        id, Symbol: string.Empty, DwellMs: dwellMs,
        Activate: _ => { toggles.Add(id); afterToggle?.Invoke(); },
        VerticalOffset: verticalOffset, RadiusLogical: 50, Label: id, RearmOnGazeExit: rearm);

    private static void Look(EdgeButtonBarViewModel vm, PhysicalPoint p, long t, bool inGap = false, bool paused = false)
        => vm.Receive(new GazeCursorMovedMessage(new GazeFrame(p, t, IsInGap: inGap)) { IsPaused = paused });
    // GazeFrame is (Position, TimestampMicroseconds, IsInGap) — IsPaused is an init property
    // ON THE MESSAGE (GazeMessages.cs:48), not a GazeFrame member.

    private static void Hold(EdgeButtonBarViewModel vm, PhysicalPoint p, long startUs, int samples)
    {
        for (var t = 0; t < samples; t++)
            Look(vm, p, startUs + t * 100_000);
    }

    [Fact]
    public void HeldGaze_TogglesExactlyOnce()
    {
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.ShowButtons(new[] { Def("mod", toggles) }, Anchor);

        Look(vm, Anchor, 1_000_000);              // sample 1: enter
        Look(vm, Anchor, 1_100_000);              // sample 2: elapsed ≥ dwell ⇒ toggle; pending set
        Hold(vm, Anchor, 1_200_000, 20);          // held: the gate feeds hits-nothing

        Check.That(toggles).ContainsExactly("mod");
    }

    [Fact]
    public void TwoFlickers_KillTheNeverResetClock()
    {
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.ShowButtons(new[] { Def("mod", toggles) }, Anchor);

        Look(vm, Anchor, 0);                      // enter
        Look(vm, Anchor, 100_000);                // toggle #1 (100 ms ≥ 1 ms dwell); pending
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 200_000);  // flicker #1: one outside sample
        Hold(vm, Anchor, 300_000, 6);             // back inside (600 ms held — a never-reset clock that
                                                  // only counts outside samples would NOT clear here)
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 900_000);  // flicker #2: one outside sample
        Hold(vm, Anchor, 1_000_000, 8);           // held again, ≥ dwell + margin

        Check.That(toggles).ContainsExactly("mod"); // a never-reset clock mutant's exit span
                                                    // 200k→900k (700 ms ≥ D) clears the pending
                                                    // and the hold re-toggles here
    }

    [Fact]
    public void SustainedExit_ReArms_ASecondEntryTogglesAgain()
    {
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.ShowButtons(new[] { Def("mod", toggles) }, Anchor);

        Look(vm, Anchor, 0);                      // enter
        Look(vm, Anchor, 100_000);                // toggle #1 (100 ms ≥ 1 ms dwell); pending
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 200_000);  // OUTSIDE: clock starts
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 500_000);  // outside: 400 ms ≥ D ⇒ cleared
        Hold(vm, Anchor, 1_000_000, 8);           // deliberate re-entry

        Check.That(toggles).ContainsExactly("mod", "mod");
    }

    [Fact]
    public void GapSamples_RestartTheExitClock()
    {
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.ShowButtons(new[] { Def("mod", toggles) }, Anchor);

        Look(vm, Anchor, 0);
        Look(vm, Anchor, 100_000);                // toggle (100 ms ≥ 1 ms dwell); pending
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 700_000);  // OUTSIDE: clock starts
        for (var t = 0; t < 4; t++)
            Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 800_000 + t * 100_000, inGap: true); // blink
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 1_400_000); // reopen OUTSIDE: the in-gap
                                                  // samples nulled the clock, so this restarts at 1.4M
                                                  // with span 0 — without the restarts, 700k→1.4M ≥ D
                                                  // would clear the pending here
        Hold(vm, Anchor, 1_500_000, 8);           // back inside and held

        Check.That(toggles).ContainsExactly("mod"); // the gap restart kept the pending alive: no re-toggle
    }

    [Fact]
    public void Lens_NeighbourCompletes_SupersedeHeals_TheFirstButtonNotDead()
    {
        var registry = new DwellDetectorRegistry();
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(), sink: null, registry);
        // Vertical stack: shift at −spacing, ctrl at 0 — the offsets are PHYSICAL px, so the
        // spacing comes from DpiHelper (scale-independent test).
        var spacing = DpiHelper.LogicalToPhysicalY(2 * UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap);
        vm.ShowButtons(new[]
        {
            Def("shift", toggles, verticalOffset: -spacing, dwellMs: 600),
            Def("ctrl", toggles, verticalOffset: 0, dwellMs: 600),
        }, Anchor);
        foreach (var registration in registry.Registrations)
            registration.ApplyGeometry(baseExtent => baseExtent * 3); // scale-independent lens: 3× regions overlap at any DPI

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 0, 8);   // shift toggles; pending(shift)

        // The lens: ctrl-nearest but inside shift's 3× region — gaze 0.7 of the way to ctrl.
        var lensPoint = new PhysicalPoint(Anchor.X, Anchor.Y - spacing + (long)(spacing * 0.7));
        Hold(vm, lensPoint, 2_000_000, 8);          // ctrl accumulates a full dwell ⇒ ctrl toggles

        Check.That(toggles).ContainsExactly("shift", "ctrl"); // gate did not dead-zone ctrl

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 4_000_000, 8); // return to shift

        Check.That(toggles.Count(s => s == "shift")).IsEqualTo(2); // supersede healed it: no dead button
    }

    [Fact]
    public void LensReturn_KillsTheClockViaNearestHittingMutant()
    {
        var registry = new DwellDetectorRegistry();
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(), sink: null, registry);
        var spacing = DpiHelper.LogicalToPhysicalY(2 * UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap);
        vm.ShowButtons(new[] { Def("shift", toggles, verticalOffset: -spacing, dwellMs: 600) }, Anchor);
        foreach (var registration in registry.Registrations)
            registration.ApplyGeometry(baseExtent => baseExtent * 3);

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 0, 8);       // toggle; pending
        // A second NON-rearm definition gives the lens a ctrl-nearest target whose toggle
        // would be visible if the excursion dwelled it:
        vm.AddButton(Def("probe", toggles, verticalOffset: 0, dwellMs: 600, rearm: false));
        // In the lens the exit clock (region membership) does NOT run — gaze is inside shift's
        // region. A nearest-hitting mutant runs it and clears the pending after D.
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y - (long)(spacing * 0.3)), 2_000_000);
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y - (long)(spacing * 0.3)), 2_100_000);
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y - (long)(spacing * 0.3)), 2_200_000); // excursion ≥ D:
        // the nearest-hitting mutant runs the clock through the lens and clears the pending here

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 3_000_000, 8); // return + full dwell

        Check.That(toggles).ContainsExactly("shift"); // no re-toggle without a genuine exit
    }

    [Fact]
    public void PartialCredit_GatedCrossing_ClearsNeighbourDwell_NotDead()
    {
        var registry = new DwellDetectorRegistry();
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(), sink: null, registry);
        var spacing = DpiHelper.LogicalToPhysicalY(2 * UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap);
        vm.ShowButtons(new[]
        {
            Def("shift", toggles, verticalOffset: -spacing, dwellMs: 600),
            Def("ctrl", toggles, verticalOffset: 0, dwellMs: 600, rearm: false),
        }, Anchor);
        foreach (var registration in registry.Registrations)
            registration.ApplyGeometry(baseExtent => baseExtent * 3);

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 0, 8);       // shift toggles about T+600; pending
        // Ctrl accumulates a PARTIAL dwell, then the gated crossing happens INSIDE the
        // detector's 150 ms re-anchor window — so the skip mutant's kept EnteredTimestamp
        // fires on the first sample back (elapsed over dwell), while the hits-nothing feed
        // cleared it and the correct impl re-dwells from zero.
        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y), 1_000_000, 6);         // ctrl 1.0M..1.5M: elapsed 500 ms
                                                                               // at the last ungated sample
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 1_550_000);  // gated crossing (shift-nearest)
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 1_600_000);
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y), 1_620_000);            // back on ctrl: last ungated
                                                                               // sample was 1.5M, silence 120 ms
                                                                               // < 150 ms, so NO re-anchor; the
                                                                               // skip mutant's kept timestamp gives
                                                                               // elapsed 620k ≥ dwell ⇒ fires HERE
        Check.That(toggles).ContainsExactly("shift");                          // correct impl: cleared, no fire

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y), 2_000_000, 8);         // correct impl: fresh dwell
        Check.That(toggles.Count(s => s == "ctrl")).IsEqualTo(1);              // the button is not dead
    }

    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    [Fact]
    public void GatedSamples_PublishNoDwellOutcome_RealActivations_PublishTheCue()
    {
        var sink = new RecordingSink();
        var messenger = new WeakReferenceMessenger();
        var cues = new List<DwellActivationMessage>();
        messenger.Register<DwellActivationMessage>(messenger, (_, m) => cues.Add(m));
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), messenger, sink, null);
        vm.ShowButtons(new[] { Def("mod", toggles) }, Anchor);

        Hold(vm, Anchor, 0, 8);                            // real activation: outcome + cue
        Check.That(sink.Outcomes.Count(o => o.Kind == OutcomeKind.Activated)).IsEqualTo(1);
        Check.That(cues).HasSize(1);

        var before = sink.Outcomes.Count;
        Hold(vm, Anchor, 3_000_000, 8);                    // gated run: hits-nothing, no outcomes, no cue
        Check.That(sink.Outcomes.Count).IsEqualTo(before);
        Check.That(cues).HasSize(1);
        // Partial-dwell abandon suppression: build a REAL partial dwell on a second button,
        // then gate the first — the filtered crossing clears the neighbour without publishing
        // an Abandoned outcome (DwellDetector.cs:393–394).
        var toggles2 = new List<string>();
        var vm2 = new EdgeButtonBarViewModel(new EyeCtrlSettings(), messenger, sink, null);
        vm2.ShowButtons(new[]
        {
            Def("shift2", toggles2, verticalOffset: -DpiHelper.LogicalToPhysicalY(120), dwellMs: 600),
            Def("ctrl2", toggles2, verticalOffset: 0, dwellMs: 600, rearm: false),
        }, new PhysicalPoint(Anchor.X, Anchor.Y + 500));
        Hold(vm2, new PhysicalPoint(Anchor.X, Anchor.Y + 500), 10_000_000, 4); // ctrl2 partial (400 ms)
        Hold(vm2, new PhysicalPoint(Anchor.X, Anchor.Y + 500 - DpiHelper.LogicalToPhysicalY(120)), 11_000_000, 2); // shift2 toggle; pending
        Hold(vm2, new PhysicalPoint(Anchor.X, Anchor.Y + 500), 12_000_000, 2); // gated crossing
        var abandoned = sink.Outcomes.Count(o => o.Kind == OutcomeKind.Abandoned);
        Check.That(abandoned).IsEqualTo(0);               // filtered targets publish no abandon
    }

    [Fact]
    public void ReplaceButton_MissingId_NoOp_AndCooldownSurvives()
    {
        var toggles = new List<string>();
        var vmRef = new List<EdgeButtonBarViewModel>();
        // 'a's Activate mirrors production's synchronous chain: toggle, then ReplaceButton runs
        // INSIDE the detector event (this is where a ResetCooldown mutant would do its damage).
        var aDef = Def("a", toggles, dwellMs: 600, afterToggle: () =>
            vmRef.Single().ReplaceButton("a", Def("a", toggles, dwellMs: 600, rearm: false)));
        // 'b' sits on its OWN slot: same-center buttons made the round-1 version flake on the
        // ConcurrentDictionary tie-break.
        var spacing = DpiHelper.LogicalToPhysicalY(2 * UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap);
        var bCenter = new PhysicalPoint(Anchor.X, Anchor.Y - spacing);
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vmRef.Add(vm);
        vm.ShowButtons(new[] { aDef, Def("b", toggles, dwellMs: 600, verticalOffset: -spacing) }, Anchor);

        Check.ThatCode(() => vm.ReplaceButton("missing", Def("missing", toggles))).DoesNotThrow();

        Hold(vm, Anchor, 0, 8);                                // 'a' toggles about T+600; pending; ReplaceButton ran
        // Cooldown survival: 'b' cannot accumulate until the cooldown elapses (T+400 ms), so it
        // completes about T+1600 — a ShowButtons mutant (ResetCooldown) completes about T+1300.
        Hold(vm, bCenter, 700_000, 7);                         // T+700..T+1300
        Check.That(toggles).ContainsExactly("a");              // the mutant would have b here
        Hold(vm, bCenter, 1_400_000, 4);                       // correct impl: b about T+1600
        Check.That(toggles).ContainsExactly("a", "b");
    }
}
