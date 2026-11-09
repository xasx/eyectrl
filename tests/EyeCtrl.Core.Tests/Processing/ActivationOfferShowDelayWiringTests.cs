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
}
