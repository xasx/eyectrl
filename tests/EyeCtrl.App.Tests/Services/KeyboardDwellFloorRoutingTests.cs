namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The shared-detector half of the keyboard family floor (prediction bar, emoji category tabs). The
/// key grid's half is pinned in <c>EyeCtrl.ViewModels.Tests.KeyboardEffectiveDwellTests</c>; this one
/// drives the app's real routing seam, <see cref="DwellTimeRouter"/>, so a clamp that only covers the
/// key grid fails here.
///
/// <para>
/// The two paths share the 150 ms value but not the comparison: this detector restarts on
/// <c>&gt;=</c> (exactly 150 ms is a restart), while the key grid credits exactly 150 ms and restarts
/// only above it. Each test below states which boundary it models; do not let one speak for both.
/// The property the floor buys is only that a SINGLE credited silence cannot complete a dwell (the
/// clamp is per gap — N silences still contribute up to 150·N, which is accepted; see
/// <c>DwellDetector.MaxCreditedSampleGapUs</c>).
/// </para>
/// </summary>
public class KeyboardDwellFloorRoutingTests
{
    [Fact]
    public void KeyboardFamilyDetector_AtTheLowestLegalBaseline_CannotBeCompletedByACreditedStall()
    {
        // The DwellTime knob's forgiving fill shortens toward 0.6×baseline, so at the 300 ms family
        // minimum the composed value would be 180 without the routing clamp — a 149 ms credited
        // silence would then leave only 31 ms of real gaze. Drive the detector the way the app wires
        // it: registration -> router map -> samples.
        var settings = new DwellSettings
        {
            KeyboardPredictionDwellMs = AdaptationDwellRanges.KeyboardMinDwellMs // the lowest the guard admits
        };

        var adaptation = new AdaptationSettings { Enabled = true };
        var provider = new EffectiveSettingsProvider(adaptation);
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);
        provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.5); // maximum forgiving deflection

        var detector = new DwellDetector(cooldownMs: 0, context: DwellContext.KeyboardPrediction);
        var registry = new DwellDetectorRegistry();
        var activated = new List<string>();
        detector.TargetActivated += activated.Add;
        detector.AddTarget(new DwellTarget("w0", new PhysicalPoint(100, 100), 50,
            settings.KeyboardPredictionDwellMs));
        registry.Register(detector, DwellRoutingRegistration.ForDetector(
            detector, DwellContext.KeyboardPrediction,
            AdaptationDwellRanges.KeyboardMinDwellMs, AdaptationDwellRanges.KeyboardMaxDwellMs));

        using var router = new DwellTimeRouter(provider, registry); // applies the current map on construction

        var at = new PhysicalPoint(100, 100);
        detector.Update(at, 0);
        // 149 ms, not 150: THIS detector credits 149 (its operator is >=, so exactly 150 restarts).
        detector.Update(at, 149_000);
        detector.Update(at, 200_000);
        Check.WithCustomMessage(
                "The routed keyboard-family dwell completed inside the blink-credit window: the " +
                "adaptive clamp or the registration's range let the forgiving knob shorten it below 300.")
            .That(activated).IsEmpty();

        // Positive control in the same run: a continuous dwell from the credited run DOES complete,
        // so the assertion above is measured on a live detector rather than one that never fires.
        detector.Update(at, 250_000);
        detector.Update(at, 300_000);
        Check.WithCustomMessage("The target never activated, so the assertion above was vacuous.")
            .That(activated).Contains("w0");
    }

    [Fact]
    public void SharedDetector_RestartsAtExactlyTheCreditBound_UnlikeTheKeyGrid()
    {
        // Same 150 ms value, opposite operator: this detector re-anchors at >='150 ms', while the key
        // grid (KeyboardViewModel.MaxContinuousSampleGapUs) credits exactly 150 and restarts only
        // above it. A floor claim phrased at "the bound" is decided at exactly 150, so pin this
        // boundary here rather than letting the key grid's strict '>' stand in for it.
        var detector = new DwellDetector(cooldownMs: 0, context: DwellContext.KeyboardPrediction);
        var activated = new List<string>();
        detector.TargetActivated += activated.Add;
        detector.AddTarget(new DwellTarget("w0", new PhysicalPoint(100, 100), 50, dwellTimeMs: 300));
        var at = new PhysicalPoint(100, 100);

        detector.Update(at, 0);
        detector.Update(at, 150_000); // exactly the bound -> re-anchored to 150 ms, NOT credited
        detector.Update(at, 200_000);
        detector.Update(at, 250_000);
        detector.Update(at, 300_000); // 150 ms since the re-anchor; a credited 150 ms run would fire
        Check.WithCustomMessage(
                "A 150 ms silence was credited by the shared detector: it must re-anchor at the " +
                "bound (>=), or the prediction bar's floor model is the key grid's strict '>' operator.")
            .That(activated).IsEmpty();

        detector.Update(at, 350_000);
        detector.Update(at, 400_000);
        detector.Update(at, 450_000); // 300 ms since the re-anchor
        Check.WithCustomMessage("The target never activated, so the assertion above was vacuous.")
            .That(activated).Contains("w0");
    }
}
