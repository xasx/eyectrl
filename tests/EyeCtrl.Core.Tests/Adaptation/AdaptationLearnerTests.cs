namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class AdaptationLearnerTests
{
    private const long Sec = 1_000_000;
    private const long Sample = AdaptationLearner.SampleIntervalMicroseconds;

    private static (AdaptationLearner Learner, EffectiveSettingsProvider Provider) Make(string? path = null)
    {
        var settings = new EyeCtrlSettings();
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        return (new AdaptationLearner(provider, settings.Adaptation, stateFilePath: path), provider);
    }

    private static void Observe(EffectiveSettingsProvider provider, double forgivingFill)
        => provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill, 0.0);

    [Fact]
    public void FirstTick_SamplesImmediately_AndPushesAboveEpsilon()
    {
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        learner.Tick(10 * Sec);
        // one sample: learned = α·1.0 = 0.05 ≥ ε → pushed to the provider.
        var entries = provider.EnumerateLearnedFills();
        Check.That(entries).HasSize(1);
        Check.That(entries[0].Knob).IsEqualTo(AdaptationKnob.DwellTime);
        Check.That(entries[0].Fill).IsCloseTo(0.05, 1e-9);
    }

    [Fact]
    public void SubCadenceTicks_SkipSampling()
    {
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        learner.Tick(10 * Sec);
        learner.Tick(10 * Sec + 29 * Sec); // 29 s later — below the 30 s cadence → skipped
        var entries = provider.EnumerateLearnedFills();
        Check.That(entries).HasSize(1);
        Check.That(entries[0].Fill).IsCloseTo(0.05, 1e-9);
        learner.Tick(10 * Sec + 30 * Sec); // cadence reached → second sample
        Check.That(provider.EnumerateLearnedFills()[0].Fill).IsCloseTo(0.0975, 1e-9);
    }

    [Fact]
    public void SustainedMaxFill_AccumulatesAtAlphaPerSample()
    {
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        learner.Tick(Sec);
        learner.Tick(Sec + Sample);
        learner.Tick(Sec + 2 * Sample);
        // 1 − 0.95³ after three samples of observed 1.0.
        Check.That(provider.EnumerateLearnedFills()[0].Fill).IsCloseTo(0.142625, 1e-9);
    }

    [Fact]
    public void ObservedZero_DecaysTowardZero()
    {
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        learner.Tick(Sec); // sample 1: learned 0.05 (pushed)
        Observe(provider, 0.0); // struggle stopped — the momentary layer has recovered to baseline
        for (int i = 1; i <= 10; i++)
            learner.Tick(Sec + i * Sample);
        // Assert the LEARNER's value (review round 1, finding 1): the provider only sees pushes that
        // move ≥ ε, and the only push in this window fires at i=5 (0.038689…), leaving the provider
        // behind the internal value (0.0299368). The learner is the source of truth for the math;
        // provider-side rate limiting is covered by AccumulationBelowEpsilon_DefersTheProviderPush.
        Check.That(learner.Snapshot().Single(e => e.Knob == AdaptationKnob.DwellTime).Fill)
            .IsCloseTo(0.05 * Math.Pow(0.95, 10), 1e-9);
    }

    [Fact]
    public void Learned_CappedAtLearnedMaxFill()
    {
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        for (int i = 0; i <= 200; i++)
            learner.Tick(Sec + i * Sample);
        Check.That(provider.EnumerateLearnedFills()[0].Fill).IsEqualTo(AdaptationSettings.LearnedMaxFill);
    }

    [Fact]
    public void AccumulationBelowEpsilon_DefersTheProviderPush()
    {
        var (learner, provider) = Make();
        Observe(provider, 0.05); // equilibrium 0.05 > ε, but early samples are below it
        learner.Tick(Sec);
        learner.Tick(Sec + Sample);
        learner.Tick(Sec + 2 * Sample);
        learner.Tick(Sec + 3 * Sample); // 4 samples: 0.00927… still < ε
        Check.That(provider.EnumerateLearnedFills()).IsEmpty();
        learner.Tick(Sec + 4 * Sample); // 5th sample crosses ε
        Check.That(provider.EnumerateLearnedFills()).HasSize(1);
    }

    [Fact]
    public void TwoKnobsAccumulateIndependently()
    {
        var (learner, provider) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, 1.0, 0.0);
        learner.Tick(Sec);
        learner.Tick(Sec + Sample);
        // Both elevated knobs learn (each ≈ 0.0975 after two samples); the two idle knobs stay absent.
        var entries = provider.EnumerateLearnedFills();
        Check.That(entries).HasSize(2);
        Check.That(entries.Select(e => e.Knob).ToList())
            .ContainsOnlyElementsThatMatch(k => k is AdaptationKnob.DwellTime or AdaptationKnob.Smoothing);
    }

    [Fact]
    public void MomentaryTighteningAlone_NeverLearns()
    {
        var (learner, provider) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.0, 0.9);
        learner.Tick(Sec);
        learner.Tick(Sec + Sample);
        learner.Tick(Sec + 2 * Sample);
        Check.That(provider.EnumerateLearnedFills()).IsEmpty();
    }

    [Fact]
    public void ClockRestart_ResumesSamplingImmediately()
    {
        // Review round 1, finding 7: the coordinator's clock is the GAZE clock; a tracker reconnect can
        // restart it near 0. A due-time guard would block sampling for the old epoch's uptime — the
        // StruggleMonitor/GazeEngine resync class. A backward jump resyncs and samples again at once.
        var (learner, provider) = Make();
        Observe(provider, 1.0);
        learner.Tick(3600 * Sec); // an hour of uptime: sample 1 (learned 0.05)
        learner.Tick(3600 * Sec + Sample); // sample 2 (0.0975)
        learner.Tick(2 * Sec); // clock restarted near 0 — long BEFORE the next due time; must sample NOW
        // The restart tick itself is sample 3 (observed still 1.0): 0.0975 + 0.05·(1 − 0.0975).
        Check.That(learner.Snapshot().Single(e => e.Knob == AdaptationKnob.DwellTime).Fill)
            .IsCloseTo(0.142625, 1e-9);
    }

    [Fact]
    public void PerKnobDisable_ZeroesItsLearnedOffsetImmediately()
    {
        // Review round 1, finding 8 + whole-branch review finding R2: the per-knob Adapt* toggles are
        // user-facing off switches; the controller collapses a disabled knob's momentary fill at once,
        // and the learner must match SYNCHRONOUSLY on the settings change — not at the next sample,
        // which can be up to one 30 s interval away.
        var settings = new EyeCtrlSettings();
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var learner = new AdaptationLearner(provider, settings.Adaptation);
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);
        learner.Tick(Sec);
        learner.Tick(Sec + Sample);
        learner.Tick(Sec + 2 * Sample);
        Check.That(provider.EnumerateLearnedFills()).HasSize(1); // learned ≈ 0.1426

        settings.Adaptation.AdaptDwell = false;
        Check.That(provider.EnumerateLearnedFills()).IsEmpty(); // cleared by the toggle itself — no sample needed
        learner.Tick(Sec + 3 * Sample);
        Check.That(provider.EnumerateLearnedFills()).IsEmpty(); // and the sample path does not resurrect it
    }

    [Fact]
    public void NeverParticipatedSession_PersistPreservesTheFile()
    {
        // Whole-branch review finding R1: a session that never loaded and never sampled (the app run
        // with adaptation off — Load is skipped, the coordinator never ticks) must not clobber the
        // offsets a previous session earned when OnExit calls Persist.
        string path = Path.Combine(Path.GetTempPath(), "eyectrl_learner_test_" + Guid.NewGuid() + ".json");
        try
        {
            AdaptationStateFile.Save(path, new AdaptationState(
                AdaptationState.CurrentVersion, DateTimeOffset.UtcNow,
                new Dictionary<AdaptationKnob, double> { [AdaptationKnob.DwellTime] = 0.3 }));
            var (learner, _) = Make(path);

            learner.Persist(); // never loaded, never ticked

            Check.That(AdaptationStateFile.Load(path).Learned[AdaptationKnob.DwellTime]).IsCloseTo(0.3, 1e-9);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResetLearned_PersistsEvenWithoutParticipation()
    {
        // The other half of R1: the explicit reset is user intent and stays durable even in a
        // never-participated session (its wipe of the file is exactly what the user asked for).
        string path = Path.Combine(Path.GetTempPath(), "eyectrl_learner_test_" + Guid.NewGuid() + ".json");
        try
        {
            AdaptationStateFile.Save(path, new AdaptationState(
                AdaptationState.CurrentVersion, DateTimeOffset.UtcNow,
                new Dictionary<AdaptationKnob, double> { [AdaptationKnob.DwellTime] = 0.3 }));
            var (learner, provider) = Make(path);

            learner.ResetLearned();

            Check.That(provider.EnumerateLearnedFills()).IsEmpty();
            Check.That(AdaptationStateFile.Load(path).Learned.Values).ContainsOnlyElementsThatMatch(v => v == 0.0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResetLearned_ClearsProviderAndPersistsZeros()
    {
        string path = Path.Combine(Path.GetTempPath(), "eyectrl_learner_test_" + Guid.NewGuid() + ".json");
        try
        {
            var (learner, provider) = Make(path);
            Observe(provider, 1.0);
            learner.Tick(Sec);
            Check.That(provider.EnumerateLearnedFills()).HasSize(1);

            learner.ResetLearned();

            Check.That(provider.EnumerateLearnedFills()).IsEmpty();
            var state = AdaptationStateFile.Load(path);
            Check.That(state.Learned.Values).ContainsOnlyElementsThatMatch(v => v == 0.0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_AppliesPersistedFillsToProvider()
    {
        string path = Path.Combine(Path.GetTempPath(), "eyectrl_learner_test_" + Guid.NewGuid() + ".json");
        try
        {
            AdaptationStateFile.Save(path, new AdaptationState(
                AdaptationState.CurrentVersion, DateTimeOffset.UtcNow,
                new Dictionary<AdaptationKnob, double>
                {
                    [AdaptationKnob.DwellTime] = 0.2,
                    [AdaptationKnob.Smoothing] = 0.1,
                }));
            var (learner, provider) = Make(path);

            learner.Load();

            var entries = provider.EnumerateLearnedFills();
            Check.That(entries).HasSize(2);
            Check.That(entries.Single(e => e.Knob == AdaptationKnob.DwellTime).Fill).IsCloseTo(0.2, 1e-9);
            Check.That(entries.Single(e => e.Knob == AdaptationKnob.Smoothing).Fill).IsCloseTo(0.1, 1e-9);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
