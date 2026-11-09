namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;
using Microsoft.Extensions.Logging;

/// <summary>
/// Round-button B1: the <c>Offer expired unused</c> line the GazeEngine writes carries the offer's
/// <c>firstLookMs=</c> — <c>none</c> for an offer that was never looked at (the "popped up while
/// reading" count <c>tools/activation_log_metrics.py</c> reports), the show-to-first-look time
/// otherwise. Driven through the real engine wiring (detector settle → offer → expiry).
/// </summary>
public class GazeEngineOfferFirstLookLogTests
{
    private sealed class CapturingLogger : ILogger<GazeEngine>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private static (GazeEngine Engine, ScriptedGazeProvider Provider, CapturingLogger Logger) Build()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Dwell.FixationRadiusPixels = 90;
        settings.Dwell.FixationDetectionMs = 300;
        settings.Dwell.ActivationButtonDwellMs = 700;
        var provider = new ScriptedGazeProvider();
        var logger = new CapturingLogger();
        var engine = new GazeEngine(provider, new RecordingInputSimulator(), settings, logger: logger);
        engine.ConfigureActivationOffers(new PhysicalRect(0, 0, 3840, 2400), scale: 2.0);
        return (engine, provider, logger);
    }

    /// <summary>1.2 s of rest at 30 ms frames: past the product's 1000 ms show delay (round button B3),
    /// so the offer is up. B1 wrote these at 20 samples (600 ms), when the settle showed the button.</summary>
    private const int RestSamples = 40;

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

    /// <summary>A saccading stream far from the offer: no new settle while the linger runs out.</summary>
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

    private static List<string> ExpiredLines(CapturingLogger logger) =>
        logger.Lines.Where(l => l.StartsWith("Offer expired unused #", StringComparison.Ordinal)).ToList();

    [Fact]
    public void AnOfferNeverLookedAt_ExpiresWith_FirstLookMsNone()
    {
        var (engine, provider, logger) = Build();
        using var _ = engine;

        long ts = Feed(provider, new PhysicalPoint(1000, 1000), 0, samples: RestSamples);   // the rest shows the offer
        Check.That(engine.InteractionManager.ActivationOffers!.CurrentOffer).IsNotNull();
        FeedAway(provider, ts, samples: 40);                                       // > the linger → expiry

        var lines = ExpiredLines(logger);
        Check.WithCustomMessage("lines: " + string.Join(" | ", logger.Lines)).That(lines).HasSize(1);
        Check.That(lines[0].EndsWith(" firstLookMs=none", StringComparison.Ordinal)).IsTrue();
    }

    [Fact]
    public void AGlancedOffer_ExpiresWith_TheShowToFirstLookTime()
    {
        var (engine, provider, logger) = Build();
        using var _ = engine;

        long ts = Feed(provider, new PhysicalPoint(1000, 1000), 0, samples: RestSamples);
        var offer = engine.InteractionManager.ActivationOffers!.CurrentOffer!;
        long firstLookUs = ts;
        ts = Feed(provider, offer.Button, ts, samples: 4);                         // a glance, no fire
        FeedAway(provider, ts, samples: 40);

        var lines = ExpiredLines(logger);
        Check.WithCustomMessage("lines: " + string.Join(" | ", logger.Lines)).That(lines).HasSize(1);
        long expectedMs = (firstLookUs - offer.ShownAtUs) / 1000;
        Check.That(expectedMs).IsStrictlyGreaterThan(0L);
        Check.That(lines[0].EndsWith($" firstLookMs={expectedMs}", StringComparison.Ordinal)).IsTrue();
        Check.That(lines[0]).StartsWith($"Offer expired unused #{offer.Id} (dwell ");
    }
}
