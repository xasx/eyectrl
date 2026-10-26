namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Status;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;

/// <summary>
/// The runtime half of the control-status model (Wave B ticket B1): feeds the headless
/// <see cref="ControlStatusModel"/> from the live provider and the interaction state machine, and
/// publishes each change as a <see cref="ControlStatusChangedMessage"/>.
/// <para>
/// Sources, exactly the ticket's list:
/// <list type="bullet">
/// <item><c>InteractionManager.StateChanged</c> — Paused;</item>
/// <item>the provider's <c>GazePointReceived</c> with <c>GazePoint.IsValid</c> — the age of the last
/// valid sample (a timer poll turns its age into TrackingLost);</item>
/// <item>the provider's <c>IsConnected</c>, polled at 10 Hz — the separate Disconnected flag;</item>
/// <item>the provider type — Mouse means NoTracker.</item>
/// </list>
/// </para>
/// <para>
/// Subscribing to the provider directly (rather than to <c>GazeEngine.GazeSampled</c>) keeps this
/// service off the engine's offer path and makes the "fake provider" acceptance test real: a fake
/// <see cref="IGazeProvider"/> is the whole input. The handler is two fields and a lock, so it is
/// safe on the provider's callback thread.
/// </para>
/// <para>
/// Messages are posted through <c>postToUi</c> in the app (the dispatcher), because they reach WPF
/// surfaces and a chime. Tests pass no hook and receive them inline. Nothing is observed until
/// <see cref="Start"/>; call it after the gaze stream starts, so the 3 s lost window begins with the
/// stream rather than with app boot.
/// </para>
/// </summary>
public sealed class ControlStatusService : IControlStatus, IDisposable
{
    /// <summary>
    /// How often <c>IsConnected</c> and <c>CurrentState</c> are polled. 10 Hz keeps the 3 s and
    /// 10 s thresholds within ±100 ms and re-syncs any lost state notification quickly.
    /// </summary>
    public const int PollIntervalMs = 100;

    private readonly IGazeProvider _provider;
    private readonly InteractionManager _interaction;
    private readonly IMessenger _messenger;
    private readonly TimeProvider _time;
    private readonly Action<Action>? _postToUi;
    private readonly ILogger<ControlStatusService>? _logger;
    private readonly ControlStatusModel _model;

    private ITimer? _timer;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Creates the service. It does not observe anything until <see cref="Start"/>; the initial
    /// status is already correct for the provider type and the current pause state.
    /// </summary>
    /// <param name="gazeProvider">The live provider: samples, <c>IsConnected</c>, and its resolved type.</param>
    /// <param name="interactionManager">The state machine whose paused state is mirrored.</param>
    /// <param name="providerType">
    /// The RESOLVED provider type (<c>GazeProviderFactory.GetProviderType</c>, which unwraps
    /// decorators). <see cref="GazeProviderType.Mouse"/> means NoTracker.
    /// </param>
    /// <param name="messenger">Publishes <see cref="ControlStatusChangedMessage"/>.</param>
    /// <param name="timeProvider">The clock. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="postToUi">
    /// Marshals message publication to the UI thread. The app passes the dispatcher; tests omit it
    /// and receive messages synchronously.
    /// </param>
    /// <param name="logger">Optional; a faulted poll/subscriber must never take the process down.</param>
    public ControlStatusService(
        IGazeProvider gazeProvider,
        InteractionManager interactionManager,
        GazeProviderType providerType,
        IMessenger messenger,
        TimeProvider? timeProvider = null,
        Action<Action>? postToUi = null,
        ILogger<ControlStatusService>? logger = null)
    {
        _provider = gazeProvider;
        _interaction = interactionManager;
        _messenger = messenger;
        _time = timeProvider ?? TimeProvider.System;
        _postToUi = postToUi;
        _logger = logger;

        _model = new ControlStatusModel(
            providerType,
            _time,
            initiallyPaused: interactionManager.CurrentState == InteractionState.Paused);
        _model.StatusChanged += OnStatusChanged;
    }

    /// <summary>The current status. Safe to read from any thread.</summary>
    public ControlStatus Current => _model.Current;

    /// <summary>
    /// Raised on every status change, on the UI thread in the running app. Mirrors
    /// <see cref="ControlStatusChangedMessage"/> for consumers that inject this service directly.
    /// </summary>
    public event Action<ControlStatus>? Changed;

    /// <summary>
    /// Begins observing. Idempotent. Call after the gaze stream starts; the initial status is
    /// published once so a surface that subscribed during startup sees it.
    /// </summary>
    public void Start()
    {
        if (_started || _disposed)
            return;
        _started = true;

        _model.Start();
        _provider.GazePointReceived += OnGazePointReceived;
        _interaction.StateChanged += OnInteractionStateChanged;
        _timer = _time.CreateTimer(
            _ => Poll(),
            state: null,
            dueTime: TimeSpan.FromMilliseconds(PollIntervalMs),
            period: TimeSpan.FromMilliseconds(PollIntervalMs));

        // Through the model's ordered gate, not a direct send: a change that raced the subscription
        // above must never be followed by this older initial value (review 01 F5).
        _model.PublishCurrent();
    }

    /// <summary>
    /// Stops observing and releases the timer. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _timer?.Dispose();
        _timer = null;
        _provider.GazePointReceived -= OnGazePointReceived;
        _interaction.StateChanged -= OnInteractionStateChanged;
        _model.StatusChanged -= OnStatusChanged;
    }

    // The provider invokes this inside its callback lock, on the gaze thread. The model call is one
    // clock read plus one short lock; the publish is posted away from this thread, never run here.
    // Contain anything that escapes anyway: a fault here unwinds into the provider's callback, which
    // Tobii's poll loop treats as a DEVICE error (disconnect + reconnect), and the recording
    // decorator deliberately does not swallow consumer exceptions.
    private void OnGazePointReceived(GazePoint point)
    {
        try
        {
            _model.OnGazeSample(point.IsValid);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Control-status sample handling failed; the status is unchanged");
        }
    }

    // StateChanged fires from the dispatcher (pause) AND the gaze thread (fixation edges) with no
    // lock around the commit/publish pair, so the ARGUMENT can be stale by the time it arrives
    // (review 01 F1). Always read the committed CurrentState instead. Contain faults here too: this
    // is the only entry that can run on the gaze thread without a guard.
    private void OnInteractionStateChanged(InteractionState _)
    {
        try
        {
            _model.SetPaused(_interaction.CurrentState == InteractionState.Paused);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Control-status state handling failed; the status is unchanged");
        }
    }

    private void Poll()
    {
        try
        {
            // Re-sync the paused mirror on every tick: any residual read/commit race from the
            // notification path self-heals within one poll (review 01 F1).
            _model.SetPaused(_interaction.CurrentState == InteractionState.Paused);
            _model.Poll(_provider.IsConnected);
        }
        catch (Exception ex)
        {
            // A timer callback that throws is an unhandled thread-pool exception and kills the
            // process — for the owner that costs the only input channel. Keep the loop alive.
            _logger?.LogWarning(ex, "Control-status poll failed; keeping the previous status");
        }
    }

    private void OnStatusChanged(ControlStatus status) => Publish(status);

    private void Publish(ControlStatus status)
    {
        void Deliver()
        {
            try
            {
                Changed?.Invoke(status);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "A control-status Changed subscriber threw");
            }

            try
            {
                _messenger.Send(new ControlStatusChangedMessage(status.State, status.Disconnected));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "A control-status message recipient threw");
            }
        }

        if (_postToUi is null)
        {
            Deliver();
            return;
        }

        try
        {
            _postToUi(Deliver);
        }
        catch (Exception ex)
        {
            // The dispatcher can refuse work while shutting down. Losing the last status then is
            // harmless; taking down the gaze thread (where a state change can originate) is not.
            _logger?.LogDebug(ex, "Could not post a control-status change to the UI thread");
        }
    }
}
