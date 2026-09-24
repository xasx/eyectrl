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
    private sealed record CueProbe(Action OnReceive) : IRecipient<DwellActivationMessage>
    {
        public int Count { get; private set; }

        public void Receive(DwellActivationMessage message)
        {
            Count++;
            OnReceive();
        }
    }

    private static DwellDetector DwellOf(EyeCareBannerWindow banner) =>
        (DwellDetector)typeof(EyeCareBannerWindow)
            .GetField("_dwell", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(banner)!;

    [StaFact]
    public void BannerDwell_PublishesDwellActivation_WithoutShowing()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var messenger = new WeakReferenceMessenger();
        var probe = new CueProbe(() => { });
        messenger.Register(probe);
        var banner = new EyeCareBannerWindow(dwellMs: 25, messenger: messenger);

        // "start" walks the real handler path: Resolve(true) runs first (Hide on a never-shown
        // window and a null-TCS TrySetResult are both no-ops), THEN the cue publishes.
        var dwell = DwellOf(banner);
        dwell.AddTarget(new DwellTarget("start", new PhysicalPoint(50, 50), 30, 25));
        for (var i = 1; i <= 20; i++)
            banner.OnGaze(new PhysicalPoint(50, 50), i * 2_000);

        Check.That(probe.Count).IsEqualTo(1);
    }
}
