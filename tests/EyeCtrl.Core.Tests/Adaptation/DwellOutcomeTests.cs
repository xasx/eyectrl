namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

public class DwellOutcomeTests
{
    [Fact]
    public void NearMiss_IsGazeMinusTargetCenter()
    {
        var outcome = new DwellOutcome(
            TimestampMicroseconds: 1_000,
            Context: DwellContext.ActionMenu,
            TargetId: "btn",
            Kind: OutcomeKind.Abandoned,
            Reason: AbandonReason.GazeLeft,
            ProgressAtEnd: 0.5,
            GazePoint: new PhysicalPoint(120, 90),
            TargetCenter: new PhysicalPoint(100, 100),
            TargetRadius: 30,
            TargetBounds: null);

        Check.That(outcome.NearMissX).IsEqualTo(20.0);
        Check.That(outcome.NearMissY).IsEqualTo(-10.0);
        Check.That(outcome.TargetBounds.HasValue).IsFalse();
    }
}
