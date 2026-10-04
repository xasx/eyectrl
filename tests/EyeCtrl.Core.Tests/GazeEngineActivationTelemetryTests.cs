namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Ticket 8 telemetry: the activation surface's outcomes come from the BUTTON DWELL, not the fixation
/// age. Completing the button dwell reports <see cref="OutcomeKind.Activated"/> at exactly 1.0 tagged
/// <see cref="DwellContext.ActivationButton"/>; an offer that expires unused reports
/// <see cref="OutcomeKind.Abandoned"/> with <see cref="AbandonReason.GazeLeft"/> at the dwell fraction
/// it reached, and only at or past <see cref="TelemetryThresholds.AbandonProgress"/>. A plain rest —
/// however long — reports nothing at all: the age clock is replay-only since ticket 8.
/// </summary>
public class GazeEngineActivationTelemetryTests
{
    private const double HitPx = LayoutConstants.ActivationButtonHitRadiusPx;

    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    private static EyeCtrlSettings Settings()
    {
        var s = new EyeCtrlSettings();
        s.Cursor.FilterMode = GazeFilterMode.Direct;
        s.Dwell.FixationRadiusPixels = 90;
        s.Dwell.FixationDetectionMs = 300;
        s.Dwell.ActivationButtonDwellMs = 700;
        return s;
    }

    // The provider and the engine must share one instance; this factory keeps that impossible to get wrong.
    private static (GazeEngine Engine, ScriptedGazeProvider Provider, RecordingSink Sink) Build()
    {
        var sink = new RecordingSink();
        var provider = new ScriptedGazeProvider();
        var engine = new GazeEngine(provider, new RecordingInputSimulator(), Settings(), telemetry: sink);
        engine.ConfigureActivationOffers(new PhysicalRect(0, 0, 3840, 2400), scale: 2.0);
        return (engine, provider, sink);
    }

    private static long Feed(ScriptedGazeProvider p, double x, double y, long fromUs, int samples)
    {
        long ts = fromUs;
        for (int i = 0; i < samples; i++)
        {
            p.Push(new GazePoint(x, y, ts, true));
            ts += 30_000;
        }
        return ts;
    }

    /// <summary>Feeds a saccading stream far from the offer: every sample lands outside the run's
    /// radius, so no new fixation ever settles (and no replacement offer is made) while the old
    /// offer's linger runs out.</summary>
    private static long FeedAway(ScriptedGazeProvider p, long fromUs, int samples)
    {
        long ts = fromUs;
        for (int i = 0; i < samples; i++)
        {
            var point = i % 2 == 0 ? new PhysicalPoint(3400, 2200) : new PhysicalPoint(600, 300);
            p.Push(new GazePoint(point.X, point.Y, ts, true));
            ts += 30_000;
        }
        return ts;
    }

    /// <summary>Holds the gaze at P until the settle makes an offer, then returns B.</summary>
    private static PhysicalPoint SettleAtP(GazeEngine engine, ScriptedGazeProvider provider, out long nextUs)
    {
        var p = new PhysicalPoint(1000, 1000);
        nextUs = Feed(provider, p.X, p.Y, 0, samples: 20);   // 600 ms: the settle lands at ~510 ms
        var offer = engine.InteractionManager.ActivationOffers?.CurrentOffer;
        Check.That(offer).IsNotNull();
        return offer!.Button;
    }

    [Fact]
    public void ButtonDwellCompletion_ReportsActivatedAtOne_TaggedActivationButton()
    {
        var (engine, provider, sink) = Build();
        using var _ = engine;

        var b = SettleAtP(engine, provider, out long ts);
        Feed(provider, b.X, b.Y, ts, samples: 25);           // 750 ms on the button → fires

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Activated);
        Check.That(sink.Outcomes[0].Context).IsEqualTo(DwellContext.ActivationButton);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("activate");
        Check.That(sink.Outcomes[0].ProgressAtEnd).IsEqualTo(1.0);
        Check.That(sink.Outcomes[0].TargetRadius).IsEqualTo(HitPx);
        Check.That(sink.Outcomes[0].TargetCenter.X).IsCloseTo(b.X, 1e-9);
        Check.That(sink.Outcomes[0].TargetCenter.Y).IsCloseTo(b.Y, 1e-9);
    }

    [Fact]
    public void PlainHold_ReportsNothing()
    {
        // The resting case: the age clock passes its threshold twice over while the gaze never leaves
        // P. The offer is made and never touched, so the button dwell stays 0 and no outcome exists.
        // Mutation: report from the age ledger (FixationActivated) instead of the offer fire → an
        // Activated arrives and this goes red.
        var (engine, provider, sink) = Build();
        using var _ = engine;

        var p = new PhysicalPoint(1000, 1000);
        Feed(provider, p.X, p.Y, 0, samples: 120);           // 3.6 s of rest

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void CompletionThenLeave_ReportsNoAbandonmentForTheSuccess()
    {
        // A success is not a gave-up. The completion is accepted (the adapter's accept), and the offer
        // is gone — nothing may report on the way out.
        var (engine, provider, sink) = Build();
        using var _ = engine;

        var b = SettleAtP(engine, provider, out long ts);
        ts = Feed(provider, b.X, b.Y, ts, samples: 25);
        Check.That(sink.Outcomes).HasSize(1);
        var offer = engine.InteractionManager.ActivationOffers!.CurrentOffer;
        Check.That(offer).IsNotNull();
        engine.InteractionManager.RequestActivationOfferAccept(offer!.Id);

        FeedAway(provider, ts, samples: 60);

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Activated);
    }

    [Fact]
    public void ExpiredAfterAnAttempt_ReportsAbandonedAtTheDwellProgress()
    {
        // Look at the button for ~10 samples (300 ms credited), then away. The offer dies 750 ms after
        // the last in-hit sample, and the outcome carries the fraction reached.
        var (engine, provider, sink) = Build();
        using var _ = engine;

        var b = SettleAtP(engine, provider, out long ts);
        ts = Feed(provider, b.X, b.Y, ts, samples: 10);      // ~300 ms in-hit, no fire
        Check.That(sink.Outcomes).IsEmpty();

        FeedAway(provider, ts, samples: 40);                 // > 1.2 s away → expiry

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.GazeLeft);
        Check.That(sink.Outcomes[0].Context).IsEqualTo(DwellContext.ActivationButton);
        Check.That(sink.Outcomes[0].ProgressAtEnd).IsCloseTo(300.0 / 700.0, 2e-2);
    }

    [Fact]
    public void ExpiredBelowTheAbandonThreshold_ReportsNothing()
    {
        // A brief glance the user did not commit to: below 0.3, the same threshold the age-based
        // abandonment used. Mutation: drop the threshold → the outcome appears.
        var (engine, provider, sink) = Build();
        using var _ = engine;

        var b = SettleAtP(engine, provider, out long ts);
        ts = Feed(provider, b.X, b.Y, ts, samples: 4);       // ~120 ms in-hit → 0.17
        FeedAway(provider, ts, samples: 40);                 // expiry

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void SuppressionTeardown_ExpiresNothing()
    {
        // The keyboard toggle (and every other suppression owner) clears the offer: it does not expire
        // unused, so no abandonment may be reported for it even though the gaze was on the button.
        // Mutation: don't request the clear on the suppression rising edge → the offer lingers and its
        // expiry reports.
        var (engine, provider, sink) = Build();
        using var _ = engine;

        var b = SettleAtP(engine, provider, out long ts);
        ts = Feed(provider, b.X, b.Y, ts, samples: 10);      // a real attempt, in flight

        engine.AcquireFixationSuppression(FixationSuppressor.Keyboard);
        ts = Feed(provider, b.X, b.Y, ts, samples: 60);      // the clear lands on the next sample

        engine.ReleaseFixationSuppression(FixationSuppressor.Keyboard);
        // Long enough away for an un-cleared 300 ms attempt to expire and report: the clear is what
        // this test observes, so a mutated "no clear" must surface an abandonment here.
        FeedAway(provider, ts, samples: 40);

        Check.That(sink.Outcomes).IsEmpty();
    }
}
