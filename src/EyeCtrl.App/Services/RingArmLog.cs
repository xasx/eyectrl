namespace EyeCtrl.App.Services;

using System;
using System.Collections.Concurrent;
using System.Globalization;
using EyeCtrl.App.Startup;
using EyeCtrl.Core.Models;
using EyeCtrl.Core;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The ring-arm look build's log lines (ticket ring-arm-look-build, "Log lines (exact; invariant
/// culture; physical px)"). The startup line is written by the App for every launch; this class adds
/// the per-event lines — constructed when <c>--ring-arm</c> was given or the settings file sets
/// <c>diagnostics.logRingEvents</c> (<see cref="Create"/>) — and the session scripts parse them.
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
        vm.RingStayStarted += OnStayStarted;
        vm.RingStayEnded += OnStayEnded;
    }

    /// <summary>Whether this launch logs the ring's events: an explicit <c>--ring-arm</c>, or the
    /// settings-file key <c>diagnostics.logRingEvents</c> (F2: Setup never passes the argument, so a
    /// sitting needs the key to get ring events at all).</summary>
    internal static bool LogsEvents(RingArmLaunch launch, DiagnosticsSettings diagnostics) =>
        launch.Specified || diagnostics.LogRingEvents;

    /// <summary>The App's one construction site: the event logger for the arm the App actually runs, or
    /// null when <see cref="LogsEvents"/> says this launch logs none.</summary>
    internal static RingArmLog? Create(RingArmLaunch launch, DiagnosticsSettings diagnostics,
        InteractionManager im, CursorCircleViewModel vm, Action<string> info) =>
        LogsEvents(launch, diagnostics) ? new RingArmLog(im, vm, info) : null;

    // ── The exact line formats ───────────────────────────────────────────────

    /// <summary>The one startup line, always written (the default included, so every log says which
    /// arm it ran): the readout detects a silent fallback — a relaunch that no longer runs the
    /// requested arm — by it. It names the stay's zone (B's no-move zone hit + R, with the hit radius,
    /// or the P-B corridor) and grace, the freeze hold's grace, and whether ring events are logged.</summary>
    internal static string StartupLine(RingArmLaunch launch, bool ringEvents)
    {
        bool hybrid = launch.Arm == RingArm.Hybrid;
        return string.Format(CultureInfo.InvariantCulture,
            "Ring arm: {0} (pin={1}, hold={2}, stay zone={3}, stay grace={4} ms, freeze grace={5} ms once proven {6} ms, glideOut=0, ringEvents={7}) [source: {8}]",
            launch.Arm switch
            {
                RingArm.Hybrid => "hybrid",
                RingArm.RunOnly => "run-only",
                _ => "today",
            },
            launch.PinPolicy == RingPinPolicy.RunOnly ? "RunOnly" : "OfferFollowing",
            hybrid ? "HybridFreeze" : "None",
            hybrid
                ? string.Format(CultureInfo.InvariantCulture, "hit+R (hit {0:0} px) or corridor {1:0.##}R",
                    LayoutConstants.ActivationButtonHitRadiusPx, CursorCircleViewModel.HybridStayCorridorFactor)
                : "none",
            hybrid ? CursorCircleViewModel.HybridStayGraceMs : 0,
            hybrid ? CursorCircleViewModel.HybridFreezeGraceMs : 0,
            hybrid ? CursorCircleViewModel.HybridFreezeProvenMs : 0,
            ringEvents ? "on" : "off",
            launch.Specified ? "--ring-arm" : "default");
    }

    /// <summary>The warning for an unknown <c>--ring-arm</c> value. The look's readout matches
    /// <c>Ring arm value '(?&lt;value&gt;[^']*)' is unknown</c> (ringlook-lib.ps1) — the text is a
    /// contract, pinned by <c>UnknownArmLine_HasTheExactFormatTheReadoutMatches</c>; the tail names
    /// the fallback's arm, the hybrid default.</summary>
    internal static string UnknownArmLine(string value) =>
        string.Format(CultureInfo.InvariantCulture,
            "Ring arm value '{0}' is unknown (today|hybrid|run-only) — running hybrid.", value);

    /// <summary>A pin, from the drawn ring to the pin target.</summary>
    internal static string PinLine(bool atOffer, PhysicalPoint target, double dist) =>
        string.Format(CultureInfo.InvariantCulture,
            "Ring pin ({0}) at=({1:0},{2:0}) dist={3:0}",
            atOffer ? "offer" : "settle", target.X, target.Y, dist);

    /// <summary>A settle pin the ring refused because it is a near miss of the live offer (no
    /// distance: nothing moved). The same coordinates as <see cref="PinLine"/>.</summary>
    internal static string IgnoredPinLine(bool atOffer, PhysicalPoint target) =>
        string.Format(CultureInfo.InvariantCulture,
            "Ring pin ({0}) at=({1:0},{2:0}) ignored=near-offer",
            atOffer ? "offer" : "settle", target.X, target.Y);

    /// <summary>A release, from the drawn ring to the gaze it snaps to.</summary>
    internal static string ReleaseLine(RingReleaseCause cause, double dist) =>
        string.Format(CultureInfo.InvariantCulture,
            "Ring release ({0}) dist={1:0}", CauseName(cause), dist);

    internal static string HoldStartLine(RingHoldStartKind kind) =>
        string.Format(CultureInfo.InvariantCulture, "Ring hold start ({0})", kind switch
        {
            RingHoldStartKind.Freeze => "freeze",
            _ => kind.ToString(),
        });

    internal static string HoldEndLine(RingHoldEndReason reason, double heldMs) =>
        string.Format(CultureInfo.InvariantCulture, "Ring hold end ({0}) heldMs={1:0}", reason switch
        {
            RingHoldEndReason.Left => "left",
            RingHoldEndReason.Unproven => "unproven",
            RingHoldEndReason.OfferClosed => "offer-closed",
            _ => "hidden",
        }, heldMs);

    /// <summary>The stay began: the ring-to-gaze distance and the gaze's distance to the segment P-B
    /// (na where P is unknown).</summary>
    internal static string StayStartLine(double dist, double? off) =>
        string.Format(CultureInfo.InvariantCulture, "Ring stay start dist={0:0} off={1}", dist, OffText(off));

    internal static string StayEndLine(RingStayEndReason reason, double ms, double? off) =>
        string.Format(CultureInfo.InvariantCulture, "Ring stay end ({0}) ms={1:0} off={2}", reason switch
        {
            RingStayEndReason.Left => "left",
            RingStayEndReason.OfferClosed => "offer-closed",
            RingStayEndReason.Settled => "settled",
            _ => "hidden",
        }, ms, OffText(off));

    private static string OffText(double? off) =>
        off is { } value ? value.ToString("0", CultureInfo.InvariantCulture) : "na";

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
        else if (e.IgnoredNearOffer)
            _info(IgnoredPinLine(_lastPinAtOffer, e.Target));
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

    private void OnStayStarted(RingStayStartEvent e) => _info(StayStartLine(e.Dist, e.Off));

    private void OnStayEnded(RingStayEndEvent e) => _info(StayEndLine(e.Reason, e.Ms, e.Off));

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
        _vm.RingStayStarted -= OnStayStarted;
        _vm.RingStayEnded -= OnStayEnded;
    }
}
