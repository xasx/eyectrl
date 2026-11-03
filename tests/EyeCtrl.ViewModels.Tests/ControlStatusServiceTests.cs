namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Status;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// The runtime adapter (Wave B ticket B1): a fake provider plus a fake clock drive the real service
/// and the real <see cref="InteractionManager"/>, and the assertions are on the published
/// <see cref="ControlStatusChangedMessage"/> stream — the contract B2b and B3 will consume.
/// </summary>
public class ControlStatusServiceTests
{
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;

        private bool _isConnected;

        /// <summary>
        /// Connectivity-fault mode (fault containment): when set, reading <see cref="IsConnected"/>
        /// throws, like a tracker SDK whose connectivity query blows up mid-session.
        /// </summary>
        public bool ThrowOnIsConnected { get; set; }

        public bool IsConnected
        {
            get => ThrowOnIsConnected
                ? throw new InvalidOperationException("tracker connectivity query fault (simulated)")
                : _isConnected;
            set => _isConnected = value;
        }

        /// <summary>The provider-observed false→true edges (B3/F4 hardening), settable by a test.</summary>
        public long ConnectEdgeCount { get; set; }

        public void Start() => IsConnected = true;

        public void Stop() => IsConnected = false;

        public void Dispose() => Stop();

        public void FireGaze(bool isValid = true) =>
            GazePointReceived?.Invoke(new GazePoint(100, 100, 0, isValid));
    }

    /// <summary>Captures every log entry that carries an exception, with its level.</summary>
    private sealed class CapturingLogger : ILogger<ControlStatusService>
    {
        public List<(LogLevel Level, string Message, Exception Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private readonly WeakReferenceMessenger _messenger = new();
    private readonly FakeTimeProvider _time = new();
    private readonly InteractionManager _interaction = new();
    private readonly FakeGazeProvider _provider = new() { IsConnected = true };
    private readonly List<ControlStatusChangedMessage> _messages = new();

    private ControlStatusService NewService(
        GazeProviderType providerType = GazeProviderType.StreamEngine,
        Action<Action>? postToUi = null,
        bool mouseIsFallback = false,
        ILogger<ControlStatusService>? logger = null)
    {
        // Registered BEFORE Start: the initial status is published from Start, and a surface that
        // subscribes during startup is exactly the case the initial publish exists for.
        _messenger.Register<ControlStatusChangedMessage>(
            _messenger, (_, message) => _messages.Add(message));

        return new ControlStatusService(
            _provider, _interaction, providerType, _messenger, _time, postToUi,
            logger, mouseIsFallback: mouseIsFallback);
    }

    private static void AdvanceMs(FakeTimeProvider time, int ms) =>
        time.Advance(TimeSpan.FromMilliseconds(ms));

    // FakeTimeProvider.Advance moves the clock to the target and then runs every due timer callback
    // with that SAME timestamp; it does not step through the intermediate instants. The service's
    // poll loop is the clock's only per-tick observer, so a single big Advance makes every poll see
    // the final moment — which would make the first disconnected observation as old as the last.
    // Step one poll at a time wherever the elapsed time inside the callback is the point.
    private static void AdvancePolls(FakeTimeProvider time, int polls)
    {
        for (int i = 0; i < polls; i++)
            AdvanceMs(time, ControlStatusService.PollIntervalMs);
    }

    [Fact]
    public void AMouseFallback_IsNoTracker_AndIsPublishedOnceAtStart()
    {
        var service = NewService(GazeProviderType.Mouse, mouseIsFallback: true);

        service.Start();

        Check.That(service.Current.State).IsEqualTo(ControlState.NoTracker);
        Check.That(service.Current.Disconnected).IsFalse();
        Check.That(_messages).HasSize(1);
        Check.That(_messages[0]).IsEqualTo(
            new ControlStatusChangedMessage(ControlState.NoTracker, Disconnected: false));

        service.Dispose();
    }

    [Fact]
    public void ADeliberateMouseProvider_StartsNormal_AndAPauseReachesPaused()
    {
        // C5 H1: Mouse chosen on purpose (--test-mode is this case too) must not report NoTracker,
        // or the strip is stuck on "No eye tracker" and never reaches Paused.
        var service = NewService(GazeProviderType.Mouse);

        service.Start();

        Check.That(service.Current.State).IsEqualTo(ControlState.Normal);
        Check.That(_messages[^1].State).IsEqualTo(ControlState.Normal);

        _interaction.TogglePause();          // FreeLook -> Paused

        Check.That(service.Current.State).IsEqualTo(ControlState.Paused);
        Check.That(_messages[^1].State).IsEqualTo(ControlState.Paused);

        // The lost-gaze clock is skipped for a deliberate Mouse provider: a long silence is no
        // TrackingLost (the mouse has no gaze stream to lose).
        AdvanceMs(_time, 60_000);
        Check.That(service.Current.State).IsEqualTo(ControlState.Paused);

        _interaction.TogglePause();          // Paused -> FreeLook
        Check.That(service.Current.State).IsEqualTo(ControlState.Normal);

        service.Dispose();
    }

    [Fact]
    public void AnEyeTracker_StartsNormal_AndTheInitialStatusIsPublished()
    {
        var service = NewService();

        service.Start();

        Check.That(service.Current.State).IsEqualTo(ControlState.Normal);
        Check.That(_messages).HasSize(1);
        Check.That(_messages[0]).IsEqualTo(
            new ControlStatusChangedMessage(ControlState.Normal, Disconnected: false));

        service.Dispose();
    }

    [Fact]
    public void Start_IsIdempotent_SoOneInitialPublishOnly()
    {
        var service = NewService();

        service.Start();
        service.Start();

        Check.That(_messages).HasSize(1);

        service.Dispose();
    }

    [Fact]
    public void AThreeAndAHalfSecondGap_PublishesLost_ThenANewSamplePublishesNormal()
    {
        var service = NewService();
        service.Start();
        _provider.FireGaze();

        AdvanceMs(_time, 3_500);

        Check.That(service.Current.State).IsEqualTo(ControlState.TrackingLost);
        Check.That(_messages[^1].State).IsEqualTo(ControlState.TrackingLost);
        Check.That(_messages[^1].Disconnected).IsFalse();

        _provider.FireGaze();

        Check.That(service.Current.State).IsEqualTo(ControlState.Normal);
        Check.That(_messages[^1].State).IsEqualTo(ControlState.Normal);

        Check.That(_messages).HasSize(3);
        Check.That(_messages[0].State).IsEqualTo(ControlState.Normal);
        Check.That(_messages[1].State).IsEqualTo(ControlState.TrackingLost);
        Check.That(_messages[2].State).IsEqualTo(ControlState.Normal);

        service.Dispose();
    }

    [Fact]
    public void ElevenSecondsDisconnected_PublishesDisconnectedTrue()
    {
        var service = NewService();
        service.Start();
        _provider.FireGaze();

        _provider.IsConnected = false;
        AdvancePolls(_time, 90);

        Check.That(service.Current.State).IsEqualTo(ControlState.TrackingLost);
        Check.That(service.Current.Disconnected).IsFalse();
        Check.That(_messages).ContainsOnlyElementsThatMatch(m => !m.Disconnected);

        AdvancePolls(_time, 20);

        Check.That(service.Current.Disconnected).IsTrue();
        Check.That(_messages[^1].Disconnected).IsTrue();
        Check.That(_messages[^1].State).IsEqualTo(ControlState.TrackingLost);

        service.Dispose();
    }

    [Fact]
    public void AReconnect_ClearsDisconnectedOnTheNextPoll()
    {
        var service = NewService();
        service.Start();

        _provider.IsConnected = false;
        AdvancePolls(_time, 120);
        Check.That(service.Current.Disconnected).IsTrue();

        _provider.IsConnected = true;
        AdvancePolls(_time, 1);

        Check.That(service.Current.Disconnected).IsFalse();
        Check.That(_messages[^1].Disconnected).IsFalse();

        service.Dispose();
    }

    [Fact]
    public void The0924WalkAwayStorm_ThroughAFakeProvider_PublishesNoDisconnectAndDoesNotFlicker()
    {
        // The real storm shape: ~0.53 s disconnected, ~0.1 s connected, 4,827 times. This replay is
        // 20 cycles (12.6 s of wall time, > 10 s of accumulated false time), so only the CONTINUOUS
        // requirement can keep the flag false. Any state message at all would be a flicker.
        var service = NewService();
        service.Start();
        _provider.FireGaze();

        for (int cycle = 0; cycle < 20; cycle++)
        {
            _provider.IsConnected = false;
            AdvancePolls(_time, 5);

            _provider.IsConnected = true;
            _provider.FireGaze();
            AdvancePolls(_time, 1);

            Check.That(service.Current.Disconnected).IsFalse();
            Check.That(service.Current.State).IsEqualTo(ControlState.Normal);
        }

        Check.That(_messages).HasSize(1);   // only the initial Normal, nothing ever changed
        Check.That(_messages[0].State).IsEqualTo(ControlState.Normal);
        Check.That(_messages[0].Disconnected).IsFalse();

        service.Dispose();
    }

    [Fact]
    public void SyntheticHundredMillisecondWindows_ThatAliasEveryPoll_NeverPublishDisconnected()
    {
        // B1 review 01 F4 + B3: a constant 100 ms connected window with a 100 ms poll can keep every
        // snapshot false for minutes. The provider saw each edge; the service must pass the counter
        // through so no poll interval counts as continuously disconnected.
        var service = NewService();
        service.Start();
        _provider.FireGaze();
        _provider.IsConnected = false;

        for (int cycle = 0; cycle < 150; cycle++)
        {
            _provider.ConnectEdgeCount++;          // window opened and closed between polls
            AdvancePolls(_time, 1);
        }

        Check.That(service.Current.Disconnected).IsFalse();
        Check.That(_messages).ContainsOnlyElementsThatMatch(m => !m.Disconnected);

        service.Dispose();
    }

    [Fact]
    public void Pause_AndResume_ArePublishedImmediately()
    {
        // B1 fixup-01 (review 01 F2): no Paused-exit debounce. The strip hides on CurrentState and
        // the published status follows the same edge with no 300-400 ms lag.
        var service = NewService();
        service.Start();

        _interaction.TogglePause();          // FreeLook -> Paused
        Check.That(service.Current.State).IsEqualTo(ControlState.Paused);
        Check.That(_messages[^1].State).IsEqualTo(ControlState.Paused);

        _interaction.TogglePause();          // Paused -> FreeLook
        Check.That(service.Current.State).IsEqualTo(ControlState.Normal);

        Check.That(_messages.Select(m => m.State))
            .ContainsExactly(ControlState.Normal, ControlState.Paused, ControlState.Normal);

        service.Dispose();
    }

    [Fact]
    public void EnteringFixationOrDwelling_DoesNotReadAsPaused()
    {
        // Review 01 S5: the mirror is "CurrentState == Paused", not "CurrentState != FreeLook". In
        // production the gaze thread flips FreeLook <-> Fixation every few hundred ms, so a
        // non-FreeLook-reads-as-paused mutant would publish Paused constantly.
        var service = NewService();
        service.Start();

        _interaction.OnFixationStarted(new PhysicalPoint(500, 400));
        Check.That(_interaction.CurrentState).IsEqualTo(InteractionState.Fixation);
        Check.That(service.Current.State).IsEqualTo(ControlState.Normal);

        AdvancePolls(_time, 3);
        Check.That(service.Current.State).IsEqualTo(ControlState.Normal);
        Check.That(_messages.Select(m => m.State)).ContainsExactly(ControlState.Normal);

        service.Dispose();
    }

    [Fact]
    public void PublishingIsMarshalledThroughThePostToUiHook()
    {
        var pending = new List<Action>();
        var service = NewService(postToUi: pending.Add);

        service.Start();

        Check.That(pending).HasSize(1);
        Check.That(_messages).IsEmpty();

        pending[0]();

        Check.That(_messages).HasSize(1);
        Check.That(_messages[0].State).IsEqualTo(ControlState.Normal);

        service.Dispose();
    }

    [Fact]
    public void Dispose_StopsObservingSamplesAndConnectivity()
    {
        var service = NewService();
        service.Start();
        var publishedAtDispose = _messages.Count;

        service.Dispose();

        _provider.IsConnected = false;
        AdvanceMs(_time, 12_000);
        _provider.FireGaze();

        Check.That(_messages).HasSize(publishedAtDispose);
        Check.That(service.Current.State).IsEqualTo(ControlState.Normal);
        Check.That(service.Current.Disconnected).IsFalse();
    }

    [Fact]
    public void TheChangedEvent_MirrorsThePublishedMessages()
    {
        var changed = new List<ControlStatus>();
        var service = NewService();
        service.Changed += changed.Add;

        service.Start();
        _interaction.TogglePause();

        Check.That(changed).HasSize(2);
        Check.That(changed[0]).IsEqualTo(new ControlStatus(ControlState.Normal, Disconnected: false));
        Check.That(changed[1]).IsEqualTo(new ControlStatus(ControlState.Paused, Disconnected: false));

        service.Dispose();
    }

    // Fault containment, leg 1. The in-source comment at the poll's catch says it plainly: a timer
    // callback that throws is an unhandled thread-pool exception and KILLS THE PROCESS — for the
    // owner that costs the only input channel. A faulting IsConnected read (every 10 Hz poll) must
    // be caught, logged, and the loop must survive it so a later Disconnected is still published.
    [Fact]
    public void AFaultingIsConnectedPoll_IsContainedAndLogged_AndTheLoopStaysAlive()
    {
        var logger = new CapturingLogger();
        var service = NewService(logger: logger);
        service.Start();
        _provider.FireGaze();

        _provider.ThrowOnIsConnected = true;
        // If the poll's catch regressed, this Advance rethrows the fault out of the timer callback
        // — here it must be contained.
        Check.ThatCode(() => AdvancePolls(_time, 5)).DoesNotThrow();

        Check.That(logger.Entries).HasSize(5);   // one warning per poll, nothing else
        Check.That(logger.Entries).ContainsOnlyElementsThatMatch(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("Control-status poll failed")
            && entry.Exception is InvalidOperationException);

        // The loop is alive: heal the fault, let IsConnected read false past the 10 s threshold,
        // and the Disconnected publish still arrives.
        _provider.ThrowOnIsConnected = false;
        _provider.IsConnected = false;
        AdvancePolls(_time, 120);

        Check.That(service.Current.Disconnected).IsTrue();
        Check.That(_messages[^1].Disconnected).IsTrue();
        Check.That(logger.Entries).HasSize(5);   // no further warnings once healed

        service.Dispose();
    }

    // Fault containment, leg 2. Deliver invokes Changed BEFORE the messenger send; a throwing
    // subscriber must not skip the send, or the status strip freezes on the previous state while
    // the service keeps running.
    [Fact]
    public void AThrowingChangedSubscriber_DoesNotKeepTheMessageFromTheMessenger()
    {
        var logger = new CapturingLogger();
        var service = NewService(logger: logger);
        service.Changed += _ => throw new InvalidOperationException("subscriber fault (simulated)");

        service.Start();
        _interaction.TogglePause();          // FreeLook -> Paused

        Check.That(_messages).HasSize(2);    // the initial Normal AND the Paused still arrive
        Check.That(_messages[0].State).IsEqualTo(ControlState.Normal);
        Check.That(_messages[^1].State).IsEqualTo(ControlState.Paused);
        Check.That(logger.Entries).HasElementThatMatches(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("A control-status Changed subscriber threw")
            && entry.Exception is InvalidOperationException);

        service.Dispose();
    }

    // Fault containment, leg 3. The dispatcher can refuse work while shutting down; the refusal
    // must not unwind into the gaze callback (where a state change can originate) and must not
    // take Start down. Losing the last status then is harmless; the service must stay usable.
    [Fact]
    public void ARefusingPostToUi_IsLoggedAsDebug_AndTheServiceStaysUsable()
    {
        var logger = new CapturingLogger();
        var hookRefuses = true;
        var service = NewService(logger: logger, postToUi: deliver =>
        {
            if (hookRefuses)
                throw new InvalidOperationException("dispatcher refused during shutdown (simulated)");
            deliver();
        });

        Check.ThatCode(service.Start).DoesNotThrow();   // the initial publish rides the refused hook
        Check.That(_messages).IsEmpty();                // that one status was lost — by design
        Check.That(logger.Entries).HasElementThatMatches(entry =>
            entry.Level == LogLevel.Debug
            && entry.Message.Contains("Could not post a control-status change to the UI thread")
            && entry.Exception is InvalidOperationException);

        // The dispatcher is back; the next change must publish again.
        hookRefuses = false;
        _interaction.TogglePause();

        Check.That(_messages).HasSize(1);
        Check.That(_messages[0].State).IsEqualTo(ControlState.Paused);

        service.Dispose();
    }
}
