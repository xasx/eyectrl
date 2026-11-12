namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Round button B3, wired: <see cref="DwellSettings.OfferShowAfterMs"/> reaches the offer engine the
/// real <see cref="GazeEngine"/> builds — at the build and live — and a rest shorter than it shows no
/// button through the production path. Each test names the production one-liner mutation that turns
/// it red.
/// </summary>
public class ActivationOfferShowDelayWiringTests
{
    private const long FrameUs = 30_000;
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint P = new(1000, 1000);

    private sealed class Harness : IDisposable
    {
        private long _us;

        public Harness(EyeCtrlSettings settings)
        {
            settings.Cursor.FilterMode = GazeFilterMode.Direct;
            settings.Dwell.FixationRadiusPixels = 90;
            settings.Dwell.FixationDetectionMs = 300;
            Settings = settings;
            Engine = new GazeEngine(Provider, new RecordingInputSimulator(), settings);
            Engine.ConfigureActivationOffers(Screen, scale: 2.0);
            Engine.InteractionManager.ActivationOfferShown += Shown.Add;
        }

        public ScriptedGazeProvider Provider { get; } = new();
        public GazeEngine Engine { get; }
        public EyeCtrlSettings Settings { get; }
        public List<ActivationOffer> Shown { get; } = new();
        public ActivationOfferEngine Offers => Engine.InteractionManager.ActivationOffers!;

        public void Feed(PhysicalPoint point, int samples)
        {
            for (int i = 0; i < samples; i++)
            {
                _us += FrameUs;
                Provider.Push(new GazePoint(point.X, point.Y, _us, true));
            }
        }

        public long NowUs => _us;

        public void FeedInvalid(int samples)
        {
            for (int i = 0; i < samples; i++)
            {
                _us += FrameUs;
                Provider.Push(new GazePoint(0, 0, _us, false));
            }
        }

        /// <summary>Holds <paramref name="point"/> one sample at a time until the settle has made a
        /// candidate, and returns the settle sample's timestamp.</summary>
        public long HoldUntilTheCandidate(PhysicalPoint point)
        {
            Feed(point, 1);
            for (int i = 0; i < 40 && Offers.OfferCandidatePoint is null; i++)
                Feed(point, 1);
            Check.That(Offers.OfferCandidatePoint).IsNotNull();
            return _us;
        }

        public void Dispose() => Engine.Dispose();
    }

    [Fact]
    public void TheSetting_ReachesTheEngineAtTheBuild_WithTheDetectorsSettleAge()
    {
        // Mutation: drop "OfferShowAfterMs = _settings.Dwell.OfferShowAfterMs" from the build → the
        // engine runs the 500 default, red.
        var settings = new EyeCtrlSettings();
        settings.Dwell.OfferShowAfterMs = 1300;
        using var h = new Harness(settings);
        h.Feed(P, 1);

        Check.That(h.Offers.OfferShowAfterMs).IsEqualTo(1300);
        Check.That(h.Offers.SettleAgeMs).IsEqualTo(FixationDetector.DefaultFixationSettleMs);
    }

    [Fact]
    public void ASlowerFixationDetection_MovesTheSettleAge_TheShowDelayIsMeasuredFrom()
    {
        var settings = new EyeCtrlSettings();
        using var h = new Harness(settings);
        h.Feed(P, 1);

        settings.Dwell.FixationDetectionMs = 800;
        h.Feed(P, 1);

        Check.That(h.Offers.SettleAgeMs).IsEqualTo(800);
    }

    [Fact]
    public void ALiveChange_ReachesTheEngine_OnTheNextSample()
    {
        // Mutation: drop OfferShowAfterMs from the reconfigure handler's property list (or the apply)
        // → the engine keeps the startup value, red.
        var settings = new EyeCtrlSettings();
        using var h = new Harness(settings);
        h.Feed(P, 1);
        Check.That(h.Offers.OfferShowAfterMs).IsEqualTo(1000);

        settings.Dwell.OfferShowAfterMs = 2000;
        h.Feed(P, 1);

        Check.That(h.Offers.OfferShowAfterMs).IsEqualTo(2000);
    }

    [Fact]
    public void TheProductDefault_ShowsNothingAtTheSettle_AndTheButtonAfterAboutOneSecondOfRest()
    {
        using var h = new Harness(new EyeCtrlSettings());

        h.Feed(P, 20);   // 600 ms: settled at ~500 ms, the button today
        Check.That(h.Shown).IsEmpty();
        Check.That(h.Offers.OfferCandidatePoint).IsNotNull();

        h.Feed(P, 20);   // 1.2 s of rest
        Check.That(h.Shown).HasSize(1);
    }

    // ── fixup-01: the crossing sample must continue the run (the detector's own rule) ──

    private const long WaitAfterSettleUs = 500_000;   // 1000 ms rest − the 500 ms settle age

    [Fact]
    public void ACrossingSampleThatLeavesTheRun_ShowsNoButtonAtTheOldSpot()
    {
        // Mutation: drop the continues-run check → the button appears at P on the very sample that
        // leaves it, red.
        using var h = new Harness(new EyeCtrlSettings());
        long settleUs = h.HoldUntilTheCandidate(P);
        while (h.NowUs + FrameUs < settleUs + WaitAfterSettleUs)
            h.Feed(P, 1);

        var away = new PhysicalPoint(P.X + 300, P.Y);   // well past R = 90
        h.Feed(away, 1);                                 // the crossing sample
        Check.That(h.Shown).IsEmpty();

        h.Feed(away, 50);                                // 1.5 s at the new spot: its own button
        Check.That(h.Shown.Select(o => o.Point)).Not.Contains(P);
        Check.That(h.Shown).HasSize(1);
    }

    [Fact]
    public void ABlinkAcrossTheShowTime_ShowsTheButtonWhenTheRestRecovers()
    {
        using var h = new Harness(new EyeCtrlSettings());
        long settleUs = h.HoldUntilTheCandidate(P);
        while (h.NowUs + FrameUs < settleUs + WaitAfterSettleUs)
            h.Feed(P, 1);

        h.FeedInvalid(5);                                // 150 ms: inside the run's gap budget
        Check.That(h.Shown).IsEmpty();

        h.Feed(P, 1);
        Check.That(h.Shown).HasSize(1);
        Check.That(h.Shown[0].Point).IsEqualTo(P);
    }

    [Fact]
    public void ABlinkPastTheGapBudget_EndsTheRest_AndShowsNothing()
    {
        // Mutation: drop the continues-run check → an invalid sample past the show time shows the
        // button mid-blink, red.
        using var h = new Harness(new EyeCtrlSettings());
        long settleUs = h.HoldUntilTheCandidate(P);
        while (h.NowUs + FrameUs < settleUs + WaitAfterSettleUs)
            h.Feed(P, 1);

        h.FeedInvalid(20);                               // 600 ms: past the 500 ms gap budget
        h.Feed(P, 5);                                    // a fresh run, not yet settled

        Check.That(h.Shown).IsEmpty();
    }
}
