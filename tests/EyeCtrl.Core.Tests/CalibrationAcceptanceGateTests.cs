namespace EyeCtrl.Core.Tests.Calibration;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

/// <summary>
/// The gate that decides whether a fitted calibration is allowed to shape every gaze point the user
/// produces.
/// <para>
/// It failed once, expensively. The 2026-09-12 calibration had per-target residuals of 450–500 px and
/// one point whose collected mean sat 911 px from its target, and it was accepted — because the only
/// check was a whole-session residual budget of 20 % of the screen diagonal, which on a 3840×2400
/// display is 907 px. A calibration that wrong is worse than none: it is applied silently to every
/// sample, so the user does not get "calibration failed", they get a machine that has stopped working
/// and no reason why.
/// </para>
/// </summary>
public class CalibrationAcceptanceGateTests
{
    private static readonly PhysicalPoint Screen = new(3840, 2400);

    /// <summary>The identity map, so a test controls residuals purely through its points.</summary>
    private static AffineTransform Identity()
    {
        var t = new AffineTransform();
        t.SetParameters([1, 0, 0, 0, 1, 0]);
        return t;
    }

    private static CalibrationPoint Point(
        double tx, double ty, double gx, double gy,
        double sigma = 2, int inBand = 30, int attempted = 30) =>
        new(new PhysicalPoint(tx, ty), new PhysicalPoint(gx, gy), sigma, sigma, inBand, attempted);

    // ── The recorded failure ──────────────────────────────────────────────────

    [Fact]
    public void The20260912Calibration_IsRejected()
    {
        // The real numbers, read off calibration.json: per-target radial σ 230–546 px, and means up to
        // 911 px from target. The old gate accepted this. Reconstructed with the identity transform so
        // the residuals ARE the recorded mean errors — which is the conservative direction, since the
        // fitted transform's own residuals (450–500 px) were of the same order.
        var points = new[]
        {
            Point(768, 480, 1679.5, 480, sigma: 181, inBand: 30, attempted: 30),   // 911 px out
            Point(1920, 1200, 2078.3, 1200, sigma: 305, inBand: 30, attempted: 30),
            Point(3072, 480, 3340.4, 480, sigma: 162, inBand: 30, attempted: 30),
            Point(3072, 1920, 3355.7, 1920, sigma: 386, inBand: 30, attempted: 30),
            Point(768, 1920, 1050.8, 1920, sigma: 312, inBand: 30, attempted: 30),
        };

        var failures = CalibrationValidator.FindQualityFailures(Identity(), points, Screen);

        Check.That(failures).Not.IsEmpty();
        // Every one of the five is bad; a gate that caught only the worst would still accept a
        // calibration built from four ruined points.
        Check.That(failures).HasSize(5);
    }

    [Fact]
    public void TheOldThreshold_WouldHaveAcceptedIt()
    {
        // Pins WHY it was accepted, so the number cannot quietly drift back. 20 % of this screen's
        // diagonal is 906 px — larger than every residual in that session.
        double diagonal = Math.Sqrt((Screen.X * Screen.X) + (Screen.Y * Screen.Y));

        Check.That(diagonal * 0.20).IsStrictlyGreaterThan(900.0);
        Check.That(diagonal * CalibrationValidator.MaxResidualScreenFraction).IsStrictlyLessThan(250.0);
    }

    // ── A good calibration still passes ───────────────────────────────────────

    [Fact]
    public void AGenuinelyGoodCalibration_IsAccepted()
    {
        // The false-rejection guard. Tightening a gate is only safe if the honest case still gets
        // through, and this user cannot afford a calibration that refuses every attempt.
        var points = new[]
        {
            Point(768, 480, 780, 470, sigma: 8),
            Point(1920, 1200, 1930, 1190, sigma: 6),
            Point(3072, 480, 3060, 495, sigma: 9),
            Point(3072, 1920, 3085, 1905, sigma: 7),
            Point(768, 1920, 755, 1935, sigma: 8),
        };

        Check.That(CalibrationValidator.FindQualityFailures(Identity(), points, Screen)).IsEmpty();
    }

    [Fact]
    public void AModestlyOffButSteadyCalibration_IsStillAccepted()
    {
        // ~150 px of genuine offset with tight scatter: exactly what calibration exists to correct,
        // and it must not be mistaken for a bad session. Inside the 226 px residual budget (5 % of the
        // 4528 px diagonal on this screen).
        var points = new[]
        {
            Point(768, 480, 888, 570, sigma: 20),
            Point(1920, 1200, 2040, 1290, sigma: 20),
            Point(3072, 1920, 3192, 2010, sigma: 20),
        };

        Check.That(CalibrationValidator.FindQualityFailures(Identity(), points, Screen)).IsEmpty();
    }

    // ── Each gate fires on its own ────────────────────────────────────────────

    [Fact]
    public void AResidualInTheGapBetweenTheOldAndNewLimits_IsNowRejected()
    {
        // Lands deliberately in the window the tightening opened up: 400 px is comfortably inside the
        // old 906 px budget and well outside the new 192 px one, with σ and coverage both perfect so
        // nothing else can be doing the rejecting. Without this, the residual gate is only pinned by
        // arithmetic — the 2026-09-12 reconstruction is caught by the scatter gate first, so loosening
        // the residual back to 0.20 would leave that test green. 400 px sits between the new budget
        // (226 px) and the old one (906 px).
        var points = new[]
        {
            Point(768, 480, 1168, 480, sigma: 3),   // 400 px out, tight, fully covered
            Point(1920, 1200, 1920, 1200, sigma: 3),
            Point(3072, 1920, 3072, 1920, sigma: 3),
        };

        var failures = CalibrationValidator.FindQualityFailures(Identity(), points, Screen);

        Check.That(failures).HasSize(1);
        Check.That(failures[0].Reason).Contains("misses it by 400px");
        Check.That(failures[0].Where).IsEqualTo("the top-left point");
    }

    [Fact]
    public void ScatterIsRejected_EvenWhenTheFitLandsPerfectly()
    {
        // The case residual alone cannot see, and the reason precision is gated separately: the mean
        // sits exactly on the target, so the fit is flawless — and it was computed from samples spread
        // over 300 px, which means it describes an average of wandering rather than a place the user
        // looked.
        var points = new[]
        {
            Point(768, 480, 768, 480, sigma: 220),
            Point(1920, 1200, 1920, 1200, sigma: 3),
            Point(3072, 1920, 3072, 1920, sigma: 3),
        };

        var failures = CalibrationValidator.FindQualityFailures(Identity(), points, Screen);

        Check.That(failures).HasSize(1);
        Check.That(failures[0].Reason).Contains("scattered");
        Check.That(failures[0].Where).IsEqualTo("the top-left point");
    }

    [Fact]
    public void LowCoverageIsRejected_EvenWhenSigmaAndResidualArePerfect()
    {
        // The loophole σ leaves open. σ is computed over in-band samples only, so three samples out of
        // three hundred give a σ of 2 px and a mean dead on target. Without the coverage gate this is
        // the best-looking calibration in the file and it is built from almost nothing.
        var points = new[]
        {
            Point(768, 480, 768, 480, sigma: 2, inBand: 3, attempted: 300),
            Point(1920, 1200, 1920, 1200, sigma: 2, inBand: 30, attempted: 30),
            Point(3072, 1920, 3072, 1920, sigma: 2, inBand: 30, attempted: 30),
        };

        var failures = CalibrationValidator.FindQualityFailures(Identity(), points, Screen);

        Check.That(failures).HasSize(1);
        Check.That(failures[0].Reason).Contains("1% of the time");
    }

    [Fact]
    public void ALegacyPointWithUnknownCoverage_IsNotFailedForBeingOld()
    {
        // AttemptedSamples is 0 on every calibration written before coverage was measured. Unknown is
        // not evidence of failure, and rejecting on it would condemn calibrations that may be fine.
        var points = new[]
        {
            new CalibrationPoint(new PhysicalPoint(768, 480), new PhysicalPoint(768, 480), 2, 2, 30),
            new CalibrationPoint(new PhysicalPoint(1920, 1200), new PhysicalPoint(1920, 1200), 2, 2, 30),
        };

        Check.That(CalibrationValidator.FindQualityFailures(Identity(), points, Screen)).IsEmpty();
    }

    // ── The message ───────────────────────────────────────────────────────────

    [Fact]
    public void TheFailureMessage_NamesTheTargets_TheMetric_AndWhatToDo()
    {
        // The old message was "Calibration accuracy is too poor (NNNpx off target). Please recalibrate."
        // — true, and useless: it named no point, so the retry repeated whatever went wrong. For a user
        // whose only input is gaze, an unaimed retry is expensive.
        var points = new[]
        {
            Point(768, 480, 1679, 480, sigma: 2),                              // residual
            Point(1920, 1200, 1920, 1200, sigma: 250),                         // scatter
            Point(3072, 1920, 3072, 1920, sigma: 2, inBand: 5, attempted: 90), // coverage
        };

        var message = CalibrationValidator.DescribeFailures(
            CalibrationValidator.FindQualityFailures(Identity(), points, Screen));

        Check.That(message).Contains("the top-left point");
        Check.That(message).Contains("the centre point");
        Check.That(message).Contains("the bottom-right point");
        Check.That(message).Contains("misses it by");
        Check.That(message).Contains("scattered");
        Check.That(message).Contains("of the time");
        Check.That(message).Contains("short looks add up");   // the actionable half
    }

    [Fact]
    public void EachTarget_ReportsOneReason_NotAPileOn()
    {
        // A point that is off AND scattered AND barely covered is one problem with one fix. Listing
        // three metrics per point would bury the thing the user has to change.
        var points = new[]
        {
            Point(768, 480, 1679, 480, sigma: 300, inBand: 2, attempted: 200),
        };

        Check.That(CalibrationValidator.FindQualityFailures(Identity(), points, Screen)).HasSize(1);
    }

    [Fact]
    public void AnAcceptedCalibration_ProducesNoMessage()
    {
        Check.That(CalibrationValidator.DescribeFailures(Array.Empty<CalibrationValidator.QualityFailure>()))
             .IsEqualTo(string.Empty);
    }

    // ── The gate is actually WIRED ────────────────────────────────────────────
    //
    // Everything above tests FindQualityFailures directly, which says the gate works but not that
    // anything calls it. Deleting the three lines in CompleteSession that invoke it left every test in
    // this file — and both suites — green. These two drive the real service end to end, so the gate
    // has to be reachable from the path a calibration actually takes.

    /// <summary>
    /// Runs a whole session through the real service. <paramref name="gazeForSample"/> receives the
    /// target and the sample ordinal, so a test can hold steady or scatter.
    /// </summary>
    private static CalibrationService RunSession(
        PhysicalPoint screen, Func<PhysicalPoint, int, PhysicalPoint> gazeForSample, int samples = 30)
    {
        var service = new CalibrationService();
        service.StartSession(5, screen);
        var targets = CalibrationTargetPositionHelper.GetPositions(5, screen);

        for (int i = 0; i < targets.Count; i++)
        {
            service.BeginTarget(i, settleWindowMs: 0, maxSamples: samples);
            // The arrival sample is discarded, so the first one is put exactly on the target to make
            // arrival unambiguous, and the scatter starts after it.
            service.AddRawSample(targets[i]);
            for (int j = 0; j < samples; j++)
                service.AddRawSample(gazeForSample(targets[i], j));
            service.EndTarget();
        }

        return service;
    }

    [Fact]
    public void CompleteSession_RejectsAScatteredFit_AndLeavesNoCalibrationApplied()
    {
        // Ticket 3's first acceptance criterion, driven through the real service rather than the
        // validator: a calibration this poor must be REFUSED, and refusing must not quietly leave a
        // half-applied transform behind — a bad calibration shapes every gaze point the user makes.
        //
        // Every sample sits INSIDE the band (±180 px of 240), so R1's collection gate is satisfied and
        // this test is about R3 alone. They alternate either side of the target, so each mean lands
        // dead on it and the fit is flawless — the residual gate sees nothing wrong. What is wrong is
        // that each point was built from gaze swinging 360 px back and forth, which is scatter, and
        // scatter is why precision is gated independently of the fit.
        var service = RunSession(Screen, (t, j) => new PhysicalPoint(
            t.X + (j % 2 == 0 ? 180 : -180), t.Y));

        Check.ThatCode(() => service.CompleteSession(Screen, "DISPLAY1"))
             .Throws<InvalidOperationException>();

        // No silent fallback: nothing applied, nothing persisted in memory.
        Check.That(service.CurrentTransform).IsNull();
        Check.That(service.CurrentData).IsNull();
    }

    [Fact]
    public void CompleteSession_NamesTheFailingTargets_NotJustTheFailure()
    {
        // The message reaches the user through this path — the view model surfaces the exception text
        // verbatim — so the naming has to survive the trip, not merely exist in the validator.
        var service = RunSession(Screen, (t, j) => new PhysicalPoint(
            t.X + (j % 2 == 0 ? 180 : -180), t.Y));

        var thrown = Check.ThatCode(() => service.CompleteSession(Screen, "DISPLAY1"))
                          .Throws<InvalidOperationException>().Value;

        Check.That(thrown.Message).Contains("point");
        Check.That(thrown.Message).Contains("short looks add up");
    }

    [Fact]
    public void CompleteSession_AcceptsAGoodSession()
    {
        // The other half, and the one that makes the rejection above meaningful: a steady user with a
        // small consistent offset still gets a calibration. Without this, a gate that rejected
        // everything would pass the test above.
        var service = RunSession(Screen, (t, _) => new PhysicalPoint(t.X + 12, t.Y - 9));

        var data = service.CompleteSession(Screen, "DISPLAY1");

        Check.That(data).IsNotNull();
        Check.That(service.CurrentTransform).IsNotNull();

        // And the coverage it recorded is full — the user never left the band, so nothing about how
        // long each target lingered may show up as a quality problem.
        Check.That(data.Points).HasSize(5);
        foreach (var p in data.Points)
            Check.That(p.Coverage).IsEqualTo(1.0);
    }
}
