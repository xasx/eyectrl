namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

/// <summary>
/// App-layer guards from the PR #71 review: the dispatcher tick loop must never be able to end the session,
/// idle ticks must not run the feedback fan-out, and the adaptation runtime must only exist while the
/// feature is enabled.
/// </summary>
public class AdaptationRuntimeContainmentTests
{
    private static AdaptationCoordinator NewCoordinator(AdaptationSettings adaptation)
    {
        var settings = new EyeCtrlSettings();
        var sink = new BufferingTelemetrySink();
        var monitor = new StruggleMonitor(sink, settings.EyeCare);
        var provider = new EffectiveSettingsProvider(adaptation);
        var controller = new AdaptationController(provider, adaptation);
        return new AdaptationCoordinator(
            monitor, controller, adaptation, new TestFakes.FakeGazeProvider(), new InteractionManager());
    }

    [Fact]
    public void RunTick_ThrowingSubscriber_DoesNotPropagate()
    {
        // App.OnStartup deliberately leaves DispatcherUnhandledException unhandled ("let the process crash so
        // the supervisor's respawn owns recovery"), so an escaping throw costs this user control of their
        // machine. The cue/drift subscribers do WPF work and Serilog I/O — both can fail.
        var adaptation = new AdaptationSettings { Enabled = true };
        var coordinator = NewCoordinator(adaptation);
        coordinator.AssessmentUpdated += _ => throw new InvalidOperationException("subscriber boom");

        // A non-None assessment is needed for the raise to happen at all; drive a couple of ticks.
        Check.ThatCode(() => { coordinator.RunTick(); coordinator.RunTick(); }).Not.ThrowsAny();
    }

    [Fact]
    public void RunTick_ConsecutiveIdleTicks_RaiseAtMostOnce()
    {
        // With adaptation disabled every tick yields None. Before coalescing, the whole fan-out ran at 4 Hz
        // forever — including two WPF dependency-property writes per tick in CueOverlayWindow.Hide.
        var adaptation = new AdaptationSettings { Enabled = false };
        var coordinator = NewCoordinator(adaptation);
        int raises = 0;
        coordinator.AssessmentUpdated += _ => raises++;

        for (int i = 0; i < 20; i++)
            coordinator.RunTick();

        Check.That(raises).IsStrictlyLessThan(2);
    }

    [Fact]
    public void Activation_DisabledAtStartup_NeverStartsTheRuntime()
    {
        var adaptation = new AdaptationSettings { Enabled = false };
        int activations = 0, deactivations = 0;
        var activation = new AdaptationActivation(adaptation, _ => activations++, () => deactivations++);

        activation.StartCore(null);

        Check.That(activation.IsActive).IsFalse();
        Check.That(activations).IsEqualTo(0); // no tick timer, no overlay HWNDs, no system-wide UIA hook
    }

    [Fact]
    public void Activation_EnabledAtStartup_StartsTheRuntimeOnce()
    {
        var adaptation = new AdaptationSettings { Enabled = true };
        int activations = 0;
        var activation = new AdaptationActivation(adaptation, _ => activations++, () => { });

        activation.StartCore(null);
        activation.StartCore(null); // idempotent

        Check.That(activation.IsActive).IsTrue();
        Check.That(activations).IsEqualTo(1);
    }

    [Fact]
    public void Activation_FollowsTheEnabledEdge_AndIgnoresRepeats()
    {
        var adaptation = new AdaptationSettings { Enabled = false };
        int activations = 0, deactivations = 0;
        var activation = new AdaptationActivation(adaptation, _ => activations++, () => deactivations++);
        activation.StartCore(null);

        adaptation.Enabled = true;
        Check.That(activation.IsActive).IsTrue();
        Check.That(activations).IsEqualTo(1);

        adaptation.Posture = AdaptationPosture.Responsive; // unrelated property — no edge
        Check.That(activations).IsEqualTo(1);

        adaptation.Enabled = false;
        Check.That(activation.IsActive).IsFalse();
        Check.That(deactivations).IsEqualTo(1);

        adaptation.Enabled = true; // re-enable must restart, not double-start
        Check.That(activation.IsActive).IsTrue();
        Check.That(activations).IsEqualTo(2);
        Check.That(deactivations).IsEqualTo(1);
    }

    [Fact]
    public void Activation_FailingActivate_IsContainedAndLeavesRuntimeInert()
    {
        // An overlay-construction failure must not break app startup or the settings toggle.
        var adaptation = new AdaptationSettings { Enabled = false };
        var activation = new AdaptationActivation(
            adaptation, _ => throw new InvalidOperationException("overlay boom"), () => { });
        activation.StartCore(null);

        Check.ThatCode(() => adaptation.Enabled = true).Not.ThrowsAny();
        Check.That(activation.IsActive).IsFalse();
    }

    [Fact]
    public void Activation_Dispose_TearsDownAndStopsWatchingTheSetting()
    {
        var adaptation = new AdaptationSettings { Enabled = true };
        int activations = 0, deactivations = 0;
        var activation = new AdaptationActivation(adaptation, _ => activations++, () => deactivations++);
        activation.StartCore(null);

        activation.Dispose();
        Check.That(deactivations).IsEqualTo(1);

        adaptation.Enabled = false;
        adaptation.Enabled = true; // no longer observed
        Check.That(activations).IsEqualTo(1);
    }
}
