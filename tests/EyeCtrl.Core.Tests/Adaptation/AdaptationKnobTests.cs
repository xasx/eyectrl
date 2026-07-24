namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

public class AdaptationKnobTests
{
    [Fact]
    public void DefinesTheFourKnobFamilies()
    {
        // Locks the complete §5 knob set so the controller (1b-④b) and the provider policy table can
        // rely on it. These four are the whole set — further growth is not expected; routing (not new
        // knobs) is what lands incrementally.
        var knobs = Enum.GetValues<AdaptationKnob>();
        Check.That(knobs).HasSize(4);
        Check.That(knobs).Contains(
            AdaptationKnob.FixationRadius,
            AdaptationKnob.DwellTime,
            AdaptationKnob.Smoothing,
            AdaptationKnob.TargetGeometry);
    }
}
