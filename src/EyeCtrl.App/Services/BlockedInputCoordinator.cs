namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// C10's app-side owner of the blocked-input state: it turns the detector's transitions into the
/// banner and the ring's blocked look, routes the live gaze into the banner's dwell buttons, and
/// runs R4's non-injecting way out. All three surfaces are touched on the UI thread only.
///
/// <para>
/// A dismissal lasts for the current blocked episode: the banner stays hidden while the block
/// persists (the owner may have seen it and chosen another route), and a clear resets the flag so
/// the next block shows again. The ring's blocked look follows the DETECTOR, not the banner, so a
/// dismissed banner still leaves the honest "your pointer is not landing" signal.
/// </para>
/// </summary>
internal sealed class BlockedInputCoordinator : IDisposable
{
    private readonly BlockedInputDetector _detector;
    private readonly IBlockedInputBanner _banner;
    private readonly IEscapeWindowService _escape;
    private readonly ICursorCircleOverlay _cursorCircle;
    private readonly Dispatcher _dispatcher;
    private readonly IMessenger _messenger;
    private readonly BlockedInputGate _gate;
    private readonly IEyeCareHooks _eyeCare;
    private bool _dismissed;
    private bool _disposed;

    /// <summary>Wires the detector to the banner, the ring, the escape path and eye-care.</summary>
    /// <param name="detector">R2's state machine.</param>
    /// <param name="banner">The banner window (a DI singleton, hidden until a block).</param>
    /// <param name="escape">R4's non-injecting foreground path.</param>
    /// <param name="cursorCircle">The gaze ring, for the blocked look.</param>
    /// <param name="dispatcher">The UI dispatcher every surface call is marshalled to.</param>
    /// <param name="messenger">The bus carrying the gaze stream.</param>
    /// <param name="gate">The flag that defers the eye-care prompt while blocked.</param>
    /// <param name="eyeCare">The eye-care hooks; only its banner is dismissed (blocked wins).</param>
    public BlockedInputCoordinator(
        BlockedInputDetector detector,
        IBlockedInputBanner banner,
        IEscapeWindowService escape,
        ICursorCircleOverlay cursorCircle,
        Dispatcher dispatcher,
        IMessenger messenger,
        BlockedInputGate gate,
        IEyeCareHooks eyeCare)
    {
        _detector = detector;
        _banner = banner;
        _escape = escape;
        _cursorCircle = cursorCircle;
        _dispatcher = dispatcher;
        _messenger = messenger;
        _gate = gate;
        _eyeCare = eyeCare;

        _detector.StateChanged += OnStateChanged;
        _banner.EscapeRequested += OnEscapeRequested;
        _banner.DismissRequested += OnDismissRequested;
        _messenger.Register<BlockedInputCoordinator, GazeCursorMovedMessage>(this, OnGaze);
    }

    private void OnStateChanged(object? sender, BlockedInputState state)
    {
        try
        {
            _dispatcher.BeginInvoke(() => Apply(state));
        }
        catch (InvalidOperationException)
        {
            // The dispatcher is shut down: there is no banner to show and the app is going away.
        }
    }

    private void OnGaze(BlockedInputCoordinator recipient, GazeCursorMovedMessage message)
    {
        // C10 fixup-01 F4: a gap frame holds the last point. The banner's buttons must not complete
        // on a blink, the same rule RestartNotice, the notes and Manual Selection follow.
        if (!_disposed && !message.IsInGap)
            _banner.OnGaze(message.Position, message.TimestampMicroseconds);
    }

    /// <summary>Applies one transition to the ring and the banner (UI thread).</summary>
    internal void Apply(BlockedInputState state)
    {
        if (_disposed)
            return;

        // C10 fixup-01 F3: blocked wins over the eye-care prompt. The gate makes a break due from
        // now on wait for the clear; an eye-care banner already on screen is taken down (only the
        // banner — a running exercise keeps its own Skip exit).
        _gate.Set(state.IsBlocked);
        if (state.IsBlocked)
            _eyeCare.CancelBanner();

        _cursorCircle.SetBlocked(state.IsBlocked);

        if (!state.IsBlocked)
        {
            _dismissed = false;
            _banner.HideBlocked();
            return;
        }

        if (_dismissed)
            return;

        _banner.ShowBlocked(state);
    }

    private void OnDismissRequested(object? sender, EventArgs e) => _dismissed = true;

    private void OnEscapeRequested(object? sender, EventArgs e)
    {
        try
        {
            var target = _escape.TryGetEscapeTarget();
            if (target is not { } hwnd || hwnd == IntPtr.Zero)
            {
                Log.Warning("Blocked-input escape: no non-elevated window or desktop is available to bring forward.");
                return;
            }

            var result = _escape.TryBringToFront(hwnd);
            Log.Information(
                "Blocked-input escape: window {Hwnd} foregroundChanged={ForegroundChanged} "
                + "SetForegroundWindow={SetForegroundSucceeded} usedMinimizeRestore={UsedFallback}.",
                hwnd, result.ForegroundChanged, result.SetForegroundWindowSucceeded,
                result.UsedShowWindowFallback);
        }
        catch (Exception ex)
        {
            // The way out is the owner's emergency exit: a fault here must not take the app down.
            Log.Error(ex, "Blocked-input escape failed; the banner stays up and the block remains detectable.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _detector.StateChanged -= OnStateChanged;
        _banner.EscapeRequested -= OnEscapeRequested;
        _banner.DismissRequested -= OnDismissRequested;
        _messenger.UnregisterAll(this);
        _gate.Set(false);
        _banner.HideBlocked();
        _cursorCircle.SetBlocked(false);
    }
}
