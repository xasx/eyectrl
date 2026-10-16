namespace EyeCtrl.App.Services;

using System;
using System.Collections.Concurrent;
using System.Globalization;
using EyeCtrl.App.Startup;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;

/// <summary>
/// The ring-arm look build's log lines (ticket ring-arm-look-build, "Log lines (exact; invariant
/// culture; physical px)"). The startup line is written by the App for every launch; this class adds
/// the per-event lines — only constructed when <c>--ring-arm</c> was given — and the session scripts
/// parse them.
/// <para>
/// Each pin's origin is Core's truth (<see cref="InteractionManager.RingPinnedAtOffer"/>),
/// snapshotted where the event is raised (the gaze thread) into a queue and paired with the VM's
/// per-message record in order; the release reason reads the last pin's origin the same way. The VM
/// records carry the distances, which only the VM's drawn position can answer.
/// </para>
/// </summary>
internal sealed class RingArmLog : IDisposable
{
    private readonly InteractionManager _im;
    private readonly CursorCircleViewModel _vm;
    private readonly Action<string> _info;
    private readonly ConcurrentQueue<bool> _pinOrigins = new();
    private bool _lastPinAtOffer;
    private bool _disposed;

    public RingArmLog(InteractionManager im, CursorCircleViewModel vm, Action<string> info)
    {
        _im = im;
        _vm = vm;
        _info = info;
        im.PinRing += OnCorePin;
        vm.RingPinProcessed += OnPinProcessed;
        vm.RingReleaseProcessed += OnReleaseProcessed;
        vm.RingReleased += OnReleased;
        vm.RingHoldStarted += OnHoldStarted;
        vm.RingHoldEnded += OnHoldEnded;
    }

    // ── The exact line formats ───────────────────────────────────────────────

    /// <summary>The one startup line, always written (including for today, so every log says which
    /// arm it ran): the readout detects a silent today after a supervisor relaunch by it.</summary>
    internal static string StartupLine(RingArmLaunch launch)
    {
        bool hybrid = launch.Arm == RingArm.Hybrid;
        return string.Format(CultureInfo.InvariantCulture,
            "Ring arm: {0} (pin={1}, hold={2}, grace={3} ms once proven {4} ms, approach={5} ms, glideOut=0) [source: {6}]",
            launch.Arm switch
            {
                RingArm.Hybrid => "hybrid",
                RingArm.RunOnly => "run-only",
                _ => "today",
            },
            launch.PinPolicy == RingPinPolicy.RunOnly ? "RunOnly" : "OfferFollowing",
            hybrid ? "HybridFreeze" : "None",
            hybrid ? CursorCircleViewModel.HybridFreezeGraceMs : 0,
            hybrid ? CursorCircleViewModel.HybridFreezeProvenMs : 0,
            hybrid ? CursorCircleViewModel.HybridFreezeApproachMs : 0,
            launch.Specified ? "--ring-arm" : "default");
    }

    /// <summary>A pin, from the drawn ring to the pin target.</summary>
    internal static string PinLine(bool atOffer, PhysicalPoint target, double dist) =>
        string.Format(CultureInfo.InvariantCulture,
            "Ring pin ({0}) at=({1:0},{2:0}) dist={3:0}",
            atOffer ? "offer" : "settle", target.X, target.Y, dist);

    /// <summary>A release, from the drawn ring to the gaze it snaps to.</summary>
    internal static string ReleaseLine(RingReleaseCause cause, double dist) =>
        string.Format(CultureInfo.InvariantCulture,
            "Ring release ({0}) dist={1:0}", CauseName(cause), dist);

    internal static string HoldStartLine(RingHoldStartKind kind) =>
        string.Format(CultureInfo.InvariantCulture, "Ring hold start ({0})", kind switch
        {
            RingHoldStartKind.Keep => "keep",
            RingHoldStartKind.Approach => "approach",
            _ => "freeze",
        });

    internal static string HoldEndLine(RingHoldEndReason reason, double heldMs) =>
        string.Format(CultureInfo.InvariantCulture, "Ring hold end ({0}) heldMs={1:0}", reason switch
        {
            RingHoldEndReason.Left => "left",
            RingHoldEndReason.Unproven => "unproven",
            RingHoldEndReason.OfferClosed => "offer-closed",
            _ => "hidden",
        }, heldMs);

    private static string CauseName(RingReleaseCause cause) => cause switch
    {
        RingReleaseCause.RunEnd => "run-end",
        RingReleaseCause.OfferClosed => "offer-closed",
        RingReleaseCause.HoldEnd => "hold-end",
        _ => "state",
    };

    // ── The wiring ───────────────────────────────────────────────────────────

    // Runs where Core raises it — the gaze thread (or the dispatcher, for the exit edge): snapshot
    // the origin there, before a later pin can overwrite it.
    private void OnCorePin(PhysicalPoint point) => _pinOrigins.Enqueue(_im.RingPinnedAtOffer);

    private void OnPinProcessed(RingPinProcessedEvent e)
    {
        // One ticket per Core raise, consumed in order whether the pin applied or not, so the queue
        // can never drift; the last origin also answers the release's reason below.
        _lastPinAtOffer = _pinOrigins.TryDequeue(out var origin) && origin;
        if (e.Applied)
            _info(PinLine(_lastPinAtOffer, e.Target, e.Dist));
    }

    private void OnReleaseProcessed(RingReleaseProcessedEvent e)
    {
        // The shipped hold releases at the message: an offer pin's release is the offer's close, any
        // other the run's end. (RunOnly releases never follow an offer pin, so run-end is the truth.)
        if (e.ReleasedNow)
            _info(ReleaseLine(_lastPinAtOffer ? RingReleaseCause.OfferClosed : RingReleaseCause.RunEnd, e.Dist));
    }

    private void OnReleased(RingReleaseEvent e) => _info(ReleaseLine(e.Cause, e.Dist));

    private void OnHoldStarted(RingHoldStartEvent e) => _info(HoldStartLine(e.Kind));

    private void OnHoldEnded(RingHoldEndEvent e) => _info(HoldEndLine(e.Reason, e.HeldMs));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _im.PinRing -= OnCorePin;
        _vm.RingPinProcessed -= OnPinProcessed;
        _vm.RingReleaseProcessed -= OnReleaseProcessed;
        _vm.RingReleased -= OnReleased;
        _vm.RingHoldStarted -= OnHoldStarted;
        _vm.RingHoldEnded -= OnHoldEnded;
    }
}
