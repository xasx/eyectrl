namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// B2b acceptance: the 09-24 walk-away storm replayed through B1's REAL ControlStatusService into the
/// strip's view model, by the route the App wires (<c>ControlStatusChangedMessage</c> →
/// <c>SetControlState</c>). The eyes are gone and the tracker flaps (~0.53 s disconnected, ~0.1 s
/// connected): the strip must show its TrackingLost label ONCE and keep it, with no flicker, then
/// drop it on the first valid sample.
/// </summary>
public class ResumeStripStormTests
{
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; set; } = true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void FireGaze(bool isValid) => GazePointReceived?.Invoke(new GazePoint(100, 100, 0, isValid));
    }

    [Fact]
    public void TheWalkAwayStorm_ShowsTrackingLostOnce_WithoutFlicker()
    {
        var messenger = new WeakReferenceMessenger();
        var time = new FakeTimeProvider();
        var provider = new FakeGazeProvider();
        var strip = new ResumeStripViewModel(new EyeCtrlSettings(), messenger);
        strip.PlaceTarget(new PhysicalPoint(1920, 404), 100);
        strip.SetBandVisible(false);
        messenger.Register<ControlStatusChangedMessage>(strip, (s, msg) => ((ResumeStripViewModel)s).SetControlState(msg.State));

        var shows = 0;
        var hides = 0;
        strip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ResumeStripViewModel.IsLabelVisible)) return;
            if (strip.IsLabelVisible) shows++; else hides++;
        };

        using var service = new ControlStatusService(
            provider, new InteractionManager(), GazeProviderType.StreamEngine, messenger, time);
        service.Start();
        provider.FireGaze(isValid: true);

        // The eyes leave; the tracker flaps for 20 cycles (12.6 s).
        for (int cycle = 0; cycle < 20; cycle++)
        {
            provider.IsConnected = false;
            for (int i = 0; i < 5; i++) time.Advance(TimeSpan.FromMilliseconds(ControlStatusService.PollIntervalMs));
            provider.IsConnected = true;
            provider.FireGaze(isValid: false);
            time.Advance(TimeSpan.FromMilliseconds(ControlStatusService.PollIntervalMs));
        }

        Check.WithCustomMessage("the storm must show the TrackingLost label exactly once")
            .That(shows).IsEqualTo(1);
        Check.WithCustomMessage("no flicker: the label never drops during the storm").That(hides).IsEqualTo(0);
        Check.That(strip.Label).IsEqualTo(ResumeStripLabel.TrackingLost);

        // The eyes come back: the first valid sample clears it at once.
        provider.FireGaze(isValid: true);
        Check.That(hides).IsEqualTo(1);
        Check.That(strip.Label).IsEqualTo(ResumeStripLabel.None);
    }
}
