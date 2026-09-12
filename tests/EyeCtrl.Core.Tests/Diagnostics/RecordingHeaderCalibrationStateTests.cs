namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// What the recording header says about the session's calibration.
/// <para>
/// This is not bookkeeping. Replay reapplies the header's transform to every valid sample, so a header
/// that says "no calibration" for a calibrated session does not merely lose a label — it makes the
/// recording measure the tracker's raw coordinate space while claiming to measure what the user saw.
/// A whole operator session can be collected, analyzed and acted on before anyone notices, which is
/// exactly what happened to the 2026-09-12 recording.
/// </para>
/// </summary>
public class RecordingHeaderCalibrationStateTests
{
    /// <summary>Builds a service holding a real fitted transform, the way a loaded session does.</summary>
    private static CalibrationService Calibrated(PhysicalPoint screen)
    {
        var service = new CalibrationService();
        service.StartSession(3, screen);
        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
        for (int i = 0; i < 3; i++)
        {
            service.BeginTarget(i);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            service.EndTarget();
        }
        service.CompleteSession(screen, "DISPLAY1");
        return service;
    }

    [Fact]
    public void ACalibratedSession_IsDescribedByItsSixCoefficients()
    {
        var service = Calibrated(new PhysicalPoint(1920, 1080));

        var described = AffineTransformParameters.TryDescribe(service.CurrentTransform);

        Check.That(described).IsNotNull();

        // The same six numbers the transform itself would apply — not a rounded or reordered copy.
        // Replay multiplies coordinates by these, so a transposed pair is a silently wrong recording.
        var expected = service.CurrentTransform!.GetParameters();
        Check.That(described!.Value.A).IsEqualTo(expected[0]);
        Check.That(described.Value.B).IsEqualTo(expected[1]);
        Check.That(described.Value.C).IsEqualTo(expected[2]);
        Check.That(described.Value.D).IsEqualTo(expected[3]);
        Check.That(described.Value.E).IsEqualTo(expected[4]);
        Check.That(described.Value.F).IsEqualTo(expected[5]);
    }

    [Fact]
    public void TheCoefficientsApplyTheSameMappingAsTheTransform()
    {
        // The assertion that actually matters to replay: reconstructing the arithmetic from the header
        // must land where the live pipeline landed. Pinning field-by-field equality alone would survive
        // a swap of the two ROWS, since both halves are structurally identical.
        var service = Calibrated(new PhysicalPoint(1920, 1080));
        var c = AffineTransformParameters.TryDescribe(service.CurrentTransform)!.Value;

        var point = new PhysicalPoint(1234.5, 678.25);
        var live = service.CurrentTransform!.Apply(point);

        Check.That((c.A * point.X) + (c.B * point.Y) + c.C).IsCloseTo(live.X, 1e-9);
        Check.That((c.D * point.X) + (c.E * point.Y) + c.F).IsCloseTo(live.Y, 1e-9);
    }

    [Fact]
    public void AnUncalibratedSession_IsDescribedAsNull()
    {
        var service = new CalibrationService();

        Check.That(service.CurrentTransform).IsNull();
        Check.That(AffineTransformParameters.TryDescribe(service.CurrentTransform)).IsNull();
    }

    [Fact]
    public void AnUnknownTransformType_IsNotGuessedAt()
    {
        // Null here means something different from "uncalibrated": paired with CalibrationActive:true
        // it tells the analyzer to refuse the recording. Reading six doubles out of a non-affine
        // transform's GetParameters and applying them as an affine map would instead produce a
        // recording that replays to the wrong place while looking complete.
        Check.That(AffineTransformParameters.TryDescribe(new NonAffineTransform())).IsNull();
    }

    private sealed class NonAffineTransform : ICalibrationTransform
    {
        public PhysicalPoint Apply(PhysicalPoint rawGaze) => rawGaze;
        public double[] GetParameters() => [1, 0, 0, 0, 1, 0];   // six, and still not an affine map
        public void SetParameters(double[] parameters) { }
    }
}
