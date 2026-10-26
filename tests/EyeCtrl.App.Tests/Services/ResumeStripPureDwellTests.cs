namespace EyeCtrl.App.Tests.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// B2a end to end through the REAL engine and the REAL adapter: gaze raised on the provider, the
/// engine publishing while Paused, GazeEngineAdapter stamping IsPaused and routing the Resume
/// target's PauseToggledMessage to <c>TogglePause</c>. The acceptance row "paused, rest on Resume for
/// the effective dwell → FreeLook", and the sticky owner's rule that Resume is a PURE dwell action:
/// no click, no button event, no action request — before the resume or in the seconds after it with
/// the eyes still resting where the target was (the engine re-arms its fixation detector on
/// Paused → FreeLook).
/// </summary>
public class ResumeStripPureDwellTests
{
    private static readonly PhysicalPoint Centre = new(1920, 400);
    private static readonly PhysicalPoint Away = new(1920, 1200);

    private sealed class StubControlFinder : IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
    }

    private sealed class ActionCounter
    {
        public int Count;
    }

    [Fact]
    public void RestingOnResume_ResumesThroughTogglePause_AndNeverClicks()
    {
        var settings = new EyeCtrlSettings();
        var provider = new TestFakes.FakeGazeProvider();
        var input = new TestFakes.FakeInputSimulator();
        using var engine = new GazeEngine(provider, input, settings);
        var messenger = new WeakReferenceMessenger();
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder());
        var actions = new ActionCounter();
        messenger.Register<ActionCounter, ActionRequestedMessage>(actions, (a, _) => a.Count++);

        var vm = new ResumeStripViewModel(settings, messenger);
        vm.PlaceTarget(Centre, radiusPhysical: 100);
        vm.SetBandVisible(false);

        var im = engine.InteractionManager;
        engine.Start();
        im.TogglePause();
        vm.SetStripShown(true); // the strip window's show, which the adapter commands on Paused
        Pump();

        long now = 1_000_000;
        void Hold(PhysicalPoint at, long durationUs)
        {
            var end = now + durationUs;
            while (now < end)
            {
                now += 16_000;
                provider.Raise(new GazePoint(at.X, at.Y, now, IsValid: true));
                Pump();
            }
        }

        Hold(Away, 400_000);
        Hold(Centre, 800_000);
        Check.WithCustomMessage("Resume fired before its 1000 ms dwell")
            .That(im.CurrentState).IsEqualTo(InteractionState.Paused);

        Hold(Centre, 600_000);
        Check.WithCustomMessage("resting on Resume for its dwell must leave Paused")
            .That(im.CurrentState).IsNotEqualTo(InteractionState.Paused);
        vm.SetStripShown(false); // the strip hides with the pause

        // The eyes stay where the target was: whatever the engine does with that rest, no input
        // may be synthesized by the resume itself.
        Hold(Centre, 3_000_000);

        Check.That(input.BareClickCount).IsEqualTo(0);
        Check.That(input.ModifiedClicks).IsEmpty();
        Check.That(input.ButtonEvents).IsEmpty();
        Check.That(input.LastKeyPress).IsNull();
        Check.WithCustomMessage("the resume requested an action")
            .That(actions.Count).IsEqualTo(0);
        Check.WithCustomMessage("a single dwell must toggle exactly once")
            .That(im.CurrentState).IsNotEqualTo(InteractionState.Paused);
    }

    /// <summary>
    /// B2a fixup-01 F1 (owner: "shorter grace on Resume"), through the real engine and adapter at the
    /// owner's geometry: the disc at (1920, 404) px, r = 100 px, each look away landing 60 px past its
    /// edge (review 01, probe P8). Short visits while reading near the disc must not add up to a
    /// resume: the app stays Paused for 20 s. With A1's full 500 ms grace these patterns resumed in
    /// 1.8 s and 2.75 s.
    /// </summary>
    [Theory]
    [InlineData(400_000, 300_000)]
    [InlineData(250_000, 250_000)]
    public void ShortVisitsWhileReading_StayPausedFor20Seconds(long onUs, long offUs)
    {
        var disc = new PhysicalPoint(1920, 404);
        var lookAway = new PhysicalPoint(1920, 404 + 100 + 60);
        var settings = new EyeCtrlSettings();
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        var messenger = new WeakReferenceMessenger();
        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder());

        var vm = new ResumeStripViewModel(settings, messenger);
        vm.PlaceTarget(disc, radiusPhysical: 100);
        vm.SetBandVisible(false);

        var im = engine.InteractionManager;
        engine.Start();
        im.TogglePause();
        vm.SetStripShown(true);
        Pump();

        long now = 1_000_000;
        void Hold(PhysicalPoint at, long durationUs)
        {
            var end = now + durationUs;
            while (now < end)
            {
                now += 16_000;
                provider.Raise(new GazePoint(at.X, at.Y, now, IsValid: true));
                Pump();
            }
        }

        Hold(lookAway, 400_000); // the look away that arms the disc
        Check.That(vm.IsArmed).IsTrue();

        var stop = now + 20_000_000;
        while (now < stop)
        {
            Hold(disc, onUs);
            Hold(lookAway, offUs);
        }

        Check.WithCustomMessage($"visits of {onUs / 1000} ms on / {offUs / 1000} ms away ended the pause")
            .That(im.CurrentState).IsEqualTo(InteractionState.Paused);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
