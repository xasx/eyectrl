namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class PosturePresetsTests
{
    [Fact]
    public void Conservative_MatchesSpec()
    {
        var t = PosturePresets.For(AdaptationPosture.Conservative);
        Check.That(t.RelaxThreshold).IsEqualTo(0.50);
        Check.That(t.SustainSeconds).IsEqualTo(3);
        Check.That(t.RelaxStepFraction).IsEqualTo(0.10);
        Check.That(t.RecoverThreshold).IsEqualTo(0.25);
        Check.That(t.RecoverStepFraction).IsEqualTo(0.05);
    }

    [Fact]
    public void Balanced_MatchesSpec()
    {
        var t = PosturePresets.For(AdaptationPosture.Balanced);
        Check.That(t.RelaxThreshold).IsEqualTo(0.40);
        Check.That(t.SustainSeconds).IsEqualTo(2);
        Check.That(t.RelaxStepFraction).IsEqualTo(0.15);
        Check.That(t.RecoverThreshold).IsEqualTo(0.20);
        Check.That(t.RecoverStepFraction).IsEqualTo(0.05);
    }

    [Fact]
    public void Responsive_MatchesSpec()
    {
        var t = PosturePresets.For(AdaptationPosture.Responsive);
        Check.That(t.RelaxThreshold).IsEqualTo(0.30);
        Check.That(t.SustainSeconds).IsEqualTo(1);
        Check.That(t.RelaxStepFraction).IsEqualTo(0.20);
        Check.That(t.RecoverThreshold).IsEqualTo(0.15);
        Check.That(t.RecoverStepFraction).IsEqualTo(0.05);
    }

    [Fact]
    public void RecoverStepFraction_IsPostureIndependent()
    {
        Check.That(PosturePresets.For(AdaptationPosture.Conservative).RecoverStepFraction)
            .IsEqualTo(PosturePresets.For(AdaptationPosture.Balanced).RecoverStepFraction);
        Check.That(PosturePresets.For(AdaptationPosture.Balanced).RecoverStepFraction)
            .IsEqualTo(PosturePresets.For(AdaptationPosture.Responsive).RecoverStepFraction);
    }

    [Fact]
    public void For_HandlesEveryDefinedPosture()
    {
        // Drift guard: a new AdaptationPosture without a PosturePresets arm makes For throw for it,
        // failing this loop and forcing the arm to be added alongside the enum member.
        foreach (var posture in Enum.GetValues<AdaptationPosture>())
        {
            var tuning = PosturePresets.For(posture);
            Check.That(tuning.SustainSeconds > 0).IsTrue();
        }
    }
}
