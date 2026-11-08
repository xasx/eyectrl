namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;
using Microsoft.Extensions.Logging;

/// <summary>
/// R1 review-01 F1: a rest offer that expires is the normal look-away cancel (or a reading pause), not
/// a failed attempt, so it must NOT reach struggle telemetry as <see cref="OutcomeKind.Abandoned"/> —
/// adaptation would then shorten F and widen H, the two levers of accidental opens. The
/// <c>Offer expired unused … style=rest</c> log line stays (the sittings count it). A Button expiry
/// past the threshold still reports Abandoned.
/// </summary>
public class GazeEngineRestActivationTelemetryTests
{
    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    private sealed class RecordingLogger : ILogger<GazeEngine>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private static readonly PhysicalPoint P = new(1000, 1000);

    private static (GazeEngine Engine, ScriptedGazeProvider Provider, RecordingSink Sink, RecordingLogger Log)
        Build(ActivationStyle style)
    {
        var s = new EyeCtrlSettings();
        s.Dwell.ActivationStyle = style;
        s.Cursor.FilterMode = GazeFilterMode.Direct;
        s.Dwell.FixationRadiusPixels = 90;
        s.Dwell.FixationDetectionMs = 300;
        var sink = new RecordingSink();
        var log = new RecordingLogger();
        var provider = new ScriptedGazeProvider();
        var engine = new GazeEngine(provider, new RecordingInputSimulator(), s, logger: log, telemetry: sink);
        engine.ConfigureActivationOffers(new PhysicalRect(0, 0, 3840, 2400), scale: 2.0);
        return (engine, provider, sink, log);
    }

    private static long Feed(ScriptedGazeProvider p, PhysicalPoint point, long fromUs, int samples)
    {
        long ts = fromUs;
        for (int i = 0; i < samples; i++)
        {
            p.Push(new GazePoint(point.X, point.Y, ts, true));
            ts += 30_000;
        }
        return ts;
    }

    /// <summary>A saccading stream far from P: no run settles, and the offer runs out.</summary>
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

    [Fact]
    public void ARestExpiryPastTheThreshold_ReportsNoAbandonment_ButKeepsTheLogLine()
    {
        // F1-M (report rest expiries too) turns this red.
        var (engine, provider, sink, log) = Build(ActivationStyle.RestFill);
        using var _ = engine;

        long ts = Feed(provider, P, 0, samples: 20);          // settle at ~510 ms: the rest offer
        var offer = engine.InteractionManager.ActivationOffers!.CurrentOffer;
        Check.That(offer).IsNotNull();
        Check.That(offer!.Style).IsEqualTo(OfferPlacementStyle.AtRestPoint);
        ts = Feed(provider, P, ts, samples: 10);              // ~300 ms more filled: about 0.5 of 700
        Check.That(engine.InteractionManager.ActivationOffers.DwellProgress)
            .IsStrictlyGreaterThan(TelemetryThresholds.AbandonProgress);

        FeedAway(provider, ts, samples: 30);                   // past G: the look-away cancel

        Check.That(engine.InteractionManager.ActivationOffers.CurrentOffer).IsNull();
        Check.That(sink.Outcomes).IsEmpty();
        Check.WithCustomMessage("logged: " + string.Join(" | ", log.Lines))
            .That(log.Lines.Any(l => l.StartsWith($"Offer expired unused #{offer.Id} (dwell ", StringComparison.Ordinal)
                                     && l.EndsWith(") style=rest", StringComparison.Ordinal))).IsTrue();
    }

    [Fact]
    public void AButtonExpiryPastTheThreshold_StillReportsAbandoned()
    {
        var (engine, provider, sink, log) = Build(ActivationStyle.OfferButton);
        using var _ = engine;

        long ts = Feed(provider, P, 0, samples: 20);
        var offer = engine.InteractionManager.ActivationOffers!.CurrentOffer!;
        ts = Feed(provider, offer.Button, ts, samples: 10);   // ~300 ms on the button
        FeedAway(provider, ts, samples: 40);                   // past the 750 ms linger

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.GazeLeft);
        Check.That(log.Lines.Any(l => l.EndsWith(") style=button", StringComparison.Ordinal))).IsTrue();
    }
}
