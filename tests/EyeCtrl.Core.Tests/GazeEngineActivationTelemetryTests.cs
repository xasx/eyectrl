namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Ticket 3 item 6: the activation surface's struggle telemetry used to be emitted by the ViewModel's
/// DwellDetector, whose progress credited blink gaps (DwellDetector.cs:370-371). The clock moved into
/// the fixation rule, so the engine emits the same outcome shapes from the valid-time ledger.
/// <list type="bullet">
/// <item>Activated at exactly 1.0, tagged <see cref="DwellContext.ActivationButton"/>;</item>
/// <item>Abandoned only on a gaze-left at or past <see cref="TelemetryThresholds.AbandonProgress"/>, with
/// the valid-time fraction;</item>
/// <item>nothing for a &gt; 500 ms eyes-lost abort (no "gave up" evidence) or a sub-threshold pass-over.
/// </item>
/// </list>
/// Mutation: emit from the raw setting/clock instead of the ledger, or emit on every release.
/// </summary>
public class GazeEngineActivationTelemetryTests
{
    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    private static EyeCtrlSettings Settings()
    {
        var s = new EyeCtrlSettings();
        s.Cursor.FilterMode = GazeFilterMode.Direct;
        s.Dwell.FixationRadiusPixels = 75;
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

    [Fact]
    public void Activation_ReportsActivatedAtOne_TaggedActivationButton()
    {
        var (engine, provider, sink) = Build();
        using var _ = engine;

        Feed(provider, 1000, 1000, 0, samples: 40); // activation at ~1020 ms

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Activated);
        Check.That(sink.Outcomes[0].Context).IsEqualTo(DwellContext.ActivationButton);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("activate");
        Check.That(sink.Outcomes[0].ProgressAtEnd).IsEqualTo(1.0);
        Check.That(sink.Outcomes[0].TargetRadius).IsEqualTo(75.0);
    }

    [Fact]
    public void GazeLeftPastTheThreshold_ReportsAbandonedAtTheValidTimeProgress()
    {
        var (engine, provider, sink) = Build();
        using var _ = engine;

        long ts = Feed(provider, 1000, 1000, 0, samples: 28); // 0..810 ms → credited 810, progress 0.728
        provider.Push(new GazePoint(1400, 1000, ts, true));   // leaves r75 → released

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.GazeLeft);
        Check.That(sink.Outcomes[0].Context).IsEqualTo(DwellContext.ActivationButton);
        Check.That(sink.Outcomes[0].ProgressAtEnd).IsCloseTo((810.0 - 300.0) / 700.0, 1e-9);
    }

    [Fact]
    public void GapAbort_ReportsNoAbandonment()
    {
        var (engine, provider, sink) = Build();
        using var _ = engine;

        long ts = Feed(provider, 1000, 1000, 0, samples: 28); // 810 ms credited
        for (int i = 0; i < 34; i++)                          // ~1 s lost eyes
        {
            provider.Push(new GazePoint(0, 0, ts, false));
            ts += 30_000;
        }
        Feed(provider, 1000, 1000, ts, samples: 5);

        // A lost-eyes abort is not a "gave up on this" signal, and the validated-time clock has no
        // abandonment to report — the run simply ended.
        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void LeaveBelowTheAbandonThreshold_ReportsNothing()
    {
        var (engine, provider, sink) = Build();
        using var _ = engine;

        long ts = Feed(provider, 1000, 1000, 0, samples: 14); // 390 ms credited → progress 0.128
        provider.Push(new GazePoint(1400, 1000, ts, true));

        Check.That(sink.Outcomes).IsEmpty();
    }
}
