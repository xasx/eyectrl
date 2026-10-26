namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// Pins adopted from review 01's probes (reviewer-only in <c>C:\t\gxb1rvm</c>) and review 02's
/// (reviewer-only in <c>C:\t\gxb1rv2m</c>); every one is a renamed copy, never an edit of the pin.
/// None of these depends on the Paused-exit debounce the fixup deleted.
/// </summary>
public class ControlStatusServiceReviewPinsTests
{
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; set; } = true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void FireGaze(bool isValid = true) => GazePointReceived?.Invoke(new GazePoint(100, 100, 0, isValid));
    }

    private readonly WeakReferenceMessenger _messenger = new();
    private readonly FakeTimeProvider _time = new();
    private readonly InteractionManager _interaction = new();
    private readonly FakeGazeProvider _provider = new();
    private readonly List<ControlStatusChangedMessage> _messages = new();

    private ControlStatusService NewService()
    {
        _messenger.Register<ControlStatusChangedMessage>(_messenger, (_, m) => _messages.Add(m));
        return new ControlStatusService(_provider, _interaction, GazeProviderType.StreamEngine, _messenger, _time);
    }

    private void AdvancePolls(int polls)
    {
        for (int i = 0; i < polls; i++)
            _time.Advance(TimeSpan.FromMilliseconds(ControlStatusService.PollIntervalMs));
    }

    [Fact]
    public void AnOutOfOrderStateChanged_LeavesTheStatusInSyncWithCurrentState()
    {
        // Review 01 F1: StateChanged fires from the dispatcher AND the gaze thread with no lock
        // around the commit/publish pair, so the argument can be stale. Re-entrancy is the
        // deterministic form: an earlier subscriber re-pauses inside the FreeLook notification. The
        // service must read CurrentState, not the argument; a mutant that trusts the argument leaves
        // the status Normal while the machine is Paused.
        bool armed = false;
        _interaction.StateChanged += s =>
        {
            if (armed && s == InteractionState.FreeLook)
            {
                armed = false;
                _interaction.TogglePause();
            }
        };
        var service = NewService();
        service.Start();

        _interaction.TogglePause();         // -> Paused
        armed = true;
        _interaction.TogglePause();         // -> FreeLook, re-paused inside the notification

        // Asserted BEFORE any poll: the event handler itself must read CurrentState, not the
        // argument. (The per-poll re-sync heals this within 100 ms, which would mask an
        // argument-trusting handler in the final assertion below.)
        Check.That(_interaction.CurrentState).IsEqualTo(InteractionState.Paused);
        Check.That(service.Current.State).IsEqualTo(ControlState.Paused);

        AdvancePolls(5);
        Check.That(_interaction.CurrentState).IsEqualTo(InteractionState.Paused);
        Check.That(service.Current.State).IsEqualTo(ControlState.Paused);
        service.Dispose();
    }

    [Fact]
    public void AThrowingEarlierSubscriber_IsHealedByThePollResync()
    {
        // Review 02: a multicast StateChanged stops at the first throwing subscriber, so a fault in
        // an EARLIER subscriber means this service never hears the pause at all. Only the 10 Hz poll
        // re-sync can bring the status to Paused; removing the re-sync turns this red.
        _interaction.StateChanged += _ => throw new InvalidOperationException("earlier subscriber fault");
        var service = NewService();
        service.Start();

        try { _interaction.TogglePause(); }
        catch (InvalidOperationException) { }

        Check.That(_interaction.CurrentState).IsEqualTo(InteractionState.Paused);

        _time.Advance(TimeSpan.FromMilliseconds(ControlStatusService.PollIntervalMs));

        Check.That(service.Current.State).IsEqualTo(ControlState.Paused);
        service.Dispose();
    }

    [Fact]
    public void APausedLaunch_PublishesPausedAtStart()
    {
        _interaction.TogglePause();
        var service = NewService();
        service.Start();

        Check.That(service.Current.State).IsEqualTo(ControlState.Paused);
        Check.That(_messages[0].State).IsEqualTo(ControlState.Paused);
        service.Dispose();
    }

    [Fact]
    public void InvalidProviderSamples_DoNotHoldOffTrackingLost()
    {
        var service = NewService();
        service.Start();
        _provider.FireGaze();

        for (int i = 0; i < 35; i++)
        {
            _provider.FireGaze(isValid: false);
            AdvancePolls(1);
        }

        Check.That(service.Current.State).IsEqualTo(ControlState.TrackingLost);
        service.Dispose();
    }
}
