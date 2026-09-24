namespace EyeCtrl.App.Tests.Overlays;

using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The eye-care banner's straggler publish (spec D9): its dwell buttons fire the private
/// <c>Resolve</c> straight off the detector's TargetActivated, so without a hoisted handler
/// the cue has no place to fire from. Drives the real detector through the PUBLIC
/// <c>OnGaze</c> entry — never <c>Show</c>/<c>ShowAsync</c>: the banner's two targets are
/// added inside <c>ShowAsync</c>, but constructing the window and reflecting <c>_dwell</c>
/// needs no Show, and a shown banner is the user's only input surface (S4, never-launch
/// constraint).
/// </summary>
/// <remarks>
/// Window construction reads XAML theme tokens, so this is a
/// <see cref="WpfApplicationGate.EnsureApplicationWithTokens"/> caller on [StaFact] — and gate
/// callers never run concurrently: this class sits in "DpiHelper Tests (App)"
/// (DisableParallelization), mirroring <c>DwellRoutingRegistrationWiringTests</c>' ruling.
/// </remarks>
[Collection("DpiHelper Tests (App)")]
public class EyeCareBannerCueTests
{
    private sealed record CueProbe(Action OnReceive, Func<bool>? EffectRanAtSend = null)
        : IRecipient<DwellActivationMessage>
    {
        public int Count { get; private set; }
        public bool? EffectAtSend { get; private set; }

        public void Receive(DwellActivationMessage message)
        {
            Count++;
            EffectAtSend = EffectRanAtSend?.Invoke();
            OnReceive();
        }
    }

    private static DwellDetector DwellOf(EyeCareBannerWindow banner) =>
        (DwellDetector)typeof(EyeCareBannerWindow)
            .GetField("_dwell", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(banner)!;

    [StaFact]
    public void BannerDwell_PublishesDwellActivation_AfterTheEffect()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var messenger = new WeakReferenceMessenger();
        var banner = new EyeCareBannerWindow(dwellMs: 25, messenger: messenger);

        // Resolve's observable effect headless is the TCS: install one by reflection (ShowAsync
        // normally creates it, but calling ShowAsync would Show — forbidden), then "start" runs
        // Resolve(true) → TrySetResult BEFORE the cue publishes (spec D8).
        var tcs = new TaskCompletionSource<bool>();
        typeof(EyeCareBannerWindow)
            .GetField("_tcs", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(banner, tcs);
        var probe = new CueProbe(() => { }, EffectRanAtSend: () => tcs.Task.IsCompleted);
        messenger.Register(probe);

        var dwell = DwellOf(banner);
        dwell.AddTarget(new DwellTarget("start", new PhysicalPoint(50, 50), 30, 25));
        for (var i = 1; i <= 20; i++)
            banner.OnGaze(new PhysicalPoint(50, 50), i * 2_000);

        Check.That(probe.Count).IsEqualTo(1);
        Check.WithCustomMessage(
            "Resolve must complete the banner's result BEFORE the cue publishes (spec D8 — a " +
            "publish-first reorder leaves Count == 1 green and must not).")
            .That(probe.EffectAtSend).IsEqualTo(true);
    }

    [StaFact]
    public void NoMessengerGiven_LandsOnTheRegisteredDefaultBus()
    {
        // D9: the production factory (AppBootstrapper) passes no messenger, so the fallback IS
        // the production path — pin that it is the app's registered instance, not a private bus.
        WpfApplicationGate.EnsureApplicationWithTokens();

        var banner = new EyeCareBannerWindow(dwellMs: 25);
        var bus = typeof(EyeCareBannerWindow)
            .GetField("_messenger", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(banner);

        Check.That(bus).IsSameReferenceAs(WeakReferenceMessenger.Default);
    }
}
