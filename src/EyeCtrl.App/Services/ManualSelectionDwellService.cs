namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Manages ManualSelection dwell detection and gaze indicator updates during ManualSelection mode.
/// Subscribes to GazeCursorMovedMessage and emits ManualSelectionDwellCompletedMessage.
/// Extracts the 130+ lines of ManualSelection dwell logic from App.xaml.cs into a testable service.
/// </summary>
public class ManualSelectionDwellService : IDisposable
{
    private const string ManualSelectionDwellTargetId = "ManualSelection_dwell_target";

    private readonly IMessenger _messenger;
    private readonly ILogger<ManualSelectionDwellService> _logger;
    private readonly EyeCtrlSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly InteractionManager _interactionManager;
    private readonly IStruggleTelemetrySink? _sink;
    private readonly IDwellDetectorRegistry? _dwellRegistry;

    // State
    private DwellDetector? _dwellDetector;
    private PhysicalPoint? _manualSelectionCenter;
    private double? _manualSelectionRadius;
    private long _manualSelectionStartTimestamp;
    private bool _gracePeriodActive;
    private bool _isActive;

    // Commit-rule state. The anchor seats on stability onset and is then HELD: it is not re-centred
    // on the live gaze (that made the distance to the hit-tested point identically zero, so the
    // target radius was unreachable and every sample counted as inside). While the anchor is held
    // the gaze indicator is FROZEN on it and the click commits AT it: what the ring shows is what is
    // clicked. The running centroid of the held samples is kept only for the commit log line.
    private readonly Queue<PhysicalPoint> _stabilitySamples = new();
    private PhysicalPoint? _anchor;
    private PhysicalPoint? _commitPoint;
    private double _commitSumX;
    private double _commitSumY;
    private int _commitCount;
    private int _consecutiveOutOfHoldSamples;

    // Diagnostics only (the gated log lines): the last sample fed to the detector, to see its
    // silence restart, and whether the gaze is already outside the rim, so a bounds reset is logged
    // once per excursion rather than on every outside sample.
    private long? _lastFedTimestamp;
    private bool _outsideBounds;

    /// <summary>
    /// Onset window radius in DIP: K of the last <see cref="ManualSelectionSettings.StabilityWindowSize"/>
    /// samples must lie this close to the newest one before the anchor seats. A fixed, tight settle
    /// (30 px at 200 %, about 3.3 sigma of the owner's jitter), decoupled from the hold radius: the
    /// indicator freezes at the seat, so seating on a loose cluster would freeze the ring on the
    /// landing point while the eye and the filter are still arriving.
    /// </summary>
    internal const double OnsetRadiusLogical = 15.0;

    /// <summary>The detector's silence clamp (<c>DwellDetector.MaxCreditedSampleGapUs</c>): a gap of
    /// at least this between fed samples restarts the fill. Mirrored here only to log the restart.</summary>
    private const long DetectorSilenceRestartUs = 150 * MathConstants.MicrosecondsPerMillisecond;

    /// <summary>The prefix every gated magnifier-dwell line starts with.</summary>
    internal const string LogPrefix = "Magnifier dwell";

    public ManualSelectionDwellService(
        IMessenger messenger,
        ILogger<ManualSelectionDwellService> logger,
        EyeCtrl.Core.Settings.EyeCtrlSettings settings,
        Dispatcher dispatcher,
        InteractionManager interactionManager,
        IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null)
    {
        _messenger = messenger;
        _logger = logger;
        _settings = settings;
        _dispatcher = dispatcher;
        _interactionManager = interactionManager;
        _sink = sink;
        _dwellRegistry = dwellRegistry;

        // Register message handlers
        _messenger.Register<StartManualSelectionMessage>(this, (r, m) => OnManualSelectionStarted(m));
        _messenger.Register<EndManualSelectionMessage>(this, (r, m) => OnManualSelectionEnded());
        _messenger.Register<ManualSelectionHiddenMessage>(this, (r, m) => OnManualSelectionEnded());
        _messenger.Register<ManualSelectionRecaptureMessage>(this, (r, m) => OnRecapture());
        _messenger.Register<GazeCursorMovedMessage>(this, (r, m) => OnGazeMoved(m));
    }

    private void OnManualSelectionStarted(StartManualSelectionMessage message)
    {
        // Initialize state
        _manualSelectionCenter = message.Center;
        // The acceptance region is the VISIBLE rim: radius x magnification DIP, in physical pixels.
        // The old expression (radius x ScaleX) gated dwell at half the drawn circle, so ~75% of what
        // the user could see was dead. The invariant is "the boundary the user can see is the
        // boundary that decides", and ScaleX is what makes it hold on a scaled display — the gaze
        // point and this radius are both physical, and the drawn rim is logical.
        _manualSelectionRadius = message.Radius * message.Magnification * DpiHelper.ScaleX;
        _isActive = true;
        _gracePeriodActive = false;
        _manualSelectionStartTimestamp = 0;
        _outsideBounds = false;

        // Create DwellDetector with cooldown disabled (we handle grace period separately)
        _dwellDetector = new DwellDetector(cooldownMs: 0, context: DwellContext.ManualSelection, sink: _sink);

        // Subscribe to dwell events
        _dwellDetector.TargetActivated += OnDwellActivated;
        _dwellDetector.TargetProgress += OnDwellProgress;

        // Hold tolerance is a property of the eye and lives in SCREEN space (DIP). Magnification
        // relates screen space to content space, so it must not appear here — neither multiplied
        // nor divided. (The expression this replaces divided FixationRadiusPixels by magnification;
        // that shape is the fingerprint of treating the hold target as content-space, and not
        // scaling it is precisely what delivers the 1/magnification precision gain.)
        var holdRadiusPhysical = _settings.ManualSelection.HoldRadiusLogical * DpiHelper.ScaleX;
        var dwellTime = _settings.Dwell.ManualSelectionActionMenuDwellMs;

        // The target is created at the initial gaze point and moved to the seated anchor once
        // stability onset seats it. Samples are not fed to the detector before then, so no dwell
        // can accumulate against a point the user has not actually settled on.
        var target = new DwellTarget(
            ManualSelectionDwellTargetId,
            message.GazePoint,
            holdRadiusPhysical,
            dwellTime);

        _dwellDetector.AddTarget(target);
        ClearCommitState();

        // Per-session registration keyed by this service: re-registering replaces the previous
        // session's entry (owner-keyed registry), so only the live detector is ever routed to.
        _dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            _dwellDetector, DwellContext.ManualSelection,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs));

        _logger.LogDebug("ManualSelection dwell tracking started: center=({X}, {Y}), radius={Radius}",
            _manualSelectionCenter.Value.X, _manualSelectionCenter.Value.Y, message.Radius);
    }

    private void OnManualSelectionEnded()
    {
        // Cleanup detector
        if (_dwellDetector != null)
        {
            // Unregister BEFORE nulling: the registry entry holds the detector through its applier,
            // and a detector from a finished session must not receive any further routed maps.
            _dwellRegistry?.Unregister(this);
            _dwellDetector.TargetActivated -= OnDwellActivated;
            _dwellDetector.TargetProgress -= OnDwellProgress;
            _dwellDetector.ClearTargets();
            _dwellDetector = null;
        }

        // Reset state
        _isActive = false;
        _gracePeriodActive = false;
        _manualSelectionCenter = null;
        _manualSelectionRadius = null;
        ClearCommitState();

        _logger.LogDebug("ManualSelection dwell tracking stopped");
    }

    /// <summary>
    /// A recapture keeps the same geometry but shows fresh content underneath: any dwell progress
    /// already accumulated against the stale screenshot must not silently carry over into a commit
    /// against the new one, and the grace period restarts so gaze that happens to be resting the
    /// instant the new content appears isn't immediately eligible to commit. Also clears the
    /// commit-rule state (anchor, held samples, running centroid) the same way <see cref="ResetDwell"/>
    /// does — an anchor seated against the stale screenshot, and any commit-centroid samples
    /// accumulated against it, must not survive into a commit against the new one, and the drawn
    /// anchor mark must not be left pointing at content that no longer exists. Deliberately WITHOUT
    /// tearing down and recreating the target/session itself (geometry, radius, registry
    /// registration) — that stays exactly as it is; ticket 4 owns the targeting rule.
    /// </summary>
    private void OnRecapture()
    {
        if (!_isActive || _dwellDetector == null) return;

        var hadAnchor = _anchor is not null;
        LogReset("recapture", _anchor);
        ClearCommitState();

        _dwellDetector.ResetAllState();
        _gracePeriodActive = false;
        _manualSelectionStartTimestamp = 0;
        // Grace samples return before the bounds check, so an excursion that was running at the
        // recapture would otherwise swallow the bounds line of the next one after the new grace.
        _outsideBounds = false;

        DispatchSafe(() => _messenger.Send(new ResetManualSelectionIndicatorMessage()), "recapture-reset");

        if (hadAnchor)
            DispatchSafe(() => _messenger.Send(new ManualSelectionAnchorChangedMessage(null)), "anchor-released");
    }

    private void OnGazeMoved(GazeCursorMovedMessage message)
    {
        // Early exit if not active or wrong state
        if (!_isActive ||
            _interactionManager.CurrentState != InteractionState.ManualSelectionActive ||
            _dwellDetector == null)
            return;

        // Gap-held sample: the position is frozen, so it must not advance dwell. Skipping the whole
        // sample also keeps the grace period honest — it starts at the first REAL sample rather than
        // at a held timestamp — and the indicator simply stays where it was, which also keeps a
        // frozen ring frozen through a blink.
        if (message.IsInGap)
            return;

        var pt = message.Position;
        var ts = message.TimestampMicroseconds;

        // 1. The indicator follows the gaze (grace period included) until the anchor seats. From the
        //    seat on it is FROZEN on the anchor: no position update while _anchor is set. Every path
        //    that drops the anchor below follows this same sample again, so a release is immediate.
        if (_anchor is null)
            UpdateIndicatorPosition(pt);

        // 2. Handle grace period
        if (!_gracePeriodActive)
        {
            _manualSelectionStartTimestamp = ts;
            _gracePeriodActive = true;
        }

        var gracePeriodUs = _settings.ManualSelection.ManualSelectionGracePeriodMs * MathConstants.MicrosecondsPerMillisecond;
        if ((ts - _manualSelectionStartTimestamp) < gracePeriodUs)
            return;

        // 3. Bounds check
        if (!IsWithinManualSelectionBounds(pt))
        {
            ReleaseAndFollow("bounds", pt, ts, logOnlyOnEntry: true);
            return;
        }
        _outsideBounds = false;

        // 4. Commit rule: the anchor must exist before dwell can accumulate, and once it exists it
        //    is held. Only samples within the hold radius are fed to the detector; an isolated
        //    out-of-hold sample is skipped rather than treated as gaze-left.
        if (_anchor is null)
        {
            if (!TrySeatAnchor(pt, ts))
                return;
        }
        else if (!IsWithinHoldRadius(pt))
        {
            _consecutiveOutOfHoldSamples++;
            if (_consecutiveOutOfHoldSamples > _settings.ManualSelection.HoldOutlierSkipLimit)
            {
                // Not an isolated outlier: the user moved. Drop the anchor (unfreezing the ring onto
                // this sample), reset the dwell and start looking for a new stability onset here.
                ReleaseAndFollow("hold", pt, ts, logOnlyOnEntry: false);
                TrySeatAnchor(pt, ts);
            }
            return;
        }
        else
        {
            _consecutiveOutOfHoldSamples = 0;
            AccumulateCommitPoint(pt);
        }

        // 5. Update dwell detector. A silence of 150 ms or more since the last fed sample (a blink,
        //    or a run of skipped out-of-hold samples) restarts the fill inside the detector; the
        //    anchor, and so the frozen ring, stay. Logged here because the detector does it silently.
        if (_lastFedTimestamp is { } lastFed && ts - lastFed >= DetectorSilenceRestartUs)
            LogLine(SilenceLine(ts, (ts - lastFed) / MathConstants.MicrosecondsPerMillisecond, _anchor));
        _lastFedTimestamp = ts;
        _dwellDetector.Update(pt, ts);
    }

    /// <summary>
    /// The abandon and out-of-bounds release: reset the dwell (which drops the anchor) and put the
    /// ring back on the live gaze at once. Step 1 skipped this sample because the ring was frozen.
    /// </summary>
    private void ReleaseAndFollow(string cause, PhysicalPoint pt, long ts, bool logOnlyOnEntry)
    {
        var wasFrozen = _anchor is not null;
        if (!logOnlyOnEntry || wasFrozen || !_outsideBounds)
            LogReset(cause, _anchor, ts);
        if (logOnlyOnEntry)
            _outsideBounds = true;

        ResetDwell();
        if (wasFrozen)
            UpdateIndicatorPosition(pt);
    }

    /// <summary>
    /// Seats the dwell anchor on stability onset: at least
    /// <see cref="ManualSelectionSettings.StabilitySampleCount"/> of the last
    /// <see cref="ManualSelectionSettings.StabilityWindowSize"/> samples must lie within
    /// <see cref="OnsetRadiusLogical"/> of the newest sample. The anchor is their centroid, so
    /// seating error is ~sigma/sqrt(K). The seat sends the indicator ONE final position, the anchor,
    /// and freezes it there.
    /// </summary>
    private bool TrySeatAnchor(PhysicalPoint sample, long ts)
    {
        var sampleCount = _settings.ManualSelection.StabilitySampleCount;
        var windowSize = Math.Max(_settings.ManualSelection.StabilityWindowSize, sampleCount);

        _stabilitySamples.Enqueue(sample);
        while (_stabilitySamples.Count > windowSize)
            _stabilitySamples.Dequeue();

        var onsetRadius = OnsetRadiusLogical * DpiHelper.ScaleX;
        var onsetRadiusSq = onsetRadius * onsetRadius;

        var inlierCount = 0;
        double sumX = 0, sumY = 0;
        foreach (var candidate in _stabilitySamples)
        {
            var dx = candidate.X - sample.X;
            var dy = candidate.Y - sample.Y;
            if (dx * dx + dy * dy <= onsetRadiusSq)
            {
                inlierCount++;
                sumX += candidate.X;
                sumY += candidate.Y;
            }
        }

        if (inlierCount < sampleCount)
            return false;

        var anchor = new PhysicalPoint(sumX / inlierCount, sumY / inlierCount);
        _anchor = anchor;
        _stabilitySamples.Clear();
        _consecutiveOutOfHoldSamples = 0;

        _commitSumX = sumX;
        _commitSumY = sumY;
        _commitCount = inlierCount;
        _commitPoint = anchor;

        _dwellDetector?.UpdateTargetCenter(ManualSelectionDwellTargetId, anchor);

        _logger.LogDebug(
            "ManualSelection anchor seated at ({X}, {Y}) from {Count} stable samples",
            anchor.X, anchor.Y, inlierCount);
        LogLine(SeatLine(ts, (ts - _manualSelectionStartTimestamp) / MathConstants.MicrosecondsPerMillisecond,
            anchor, inlierCount, windowSize));

        // The freeze: the ring's last position is the anchor (within a few px of the live ring at
        // this moment), and step 1 of OnGazeMoved sends nothing more until the anchor is dropped.
        UpdateIndicatorPosition(anchor);
        DispatchSafe(() => _messenger.Send(new ManualSelectionAnchorChangedMessage(anchor)), "anchor-seated");
        return true;
    }

    private void AccumulateCommitPoint(PhysicalPoint sample)
    {
        _commitSumX += sample.X;
        _commitSumY += sample.Y;
        _commitCount++;
        _commitPoint = new PhysicalPoint(_commitSumX / _commitCount, _commitSumY / _commitCount);
    }

    private void ClearCommitState()
    {
        _anchor = null;
        _commitPoint = null;
        _commitSumX = 0;
        _commitSumY = 0;
        _commitCount = 0;
        _consecutiveOutOfHoldSamples = 0;
        _stabilitySamples.Clear();
        _lastFedTimestamp = null;
    }

    /// <param name="handler">
    /// Short discriminator for the failing call site. It is a structured property, not part of
    /// the message template: collapsing the five call sites onto a content-free <c>"{Context}"</c>
    /// template would leave log queries nothing to match on but the rendered line.
    /// </param>
    private void DispatchSafe(Action action, string handler)
    {
        void Run()
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualSelection dwell service: {Handler} failed", handler);
            }
        }

        if (_dispatcher.CheckAccess())
            Run();
        else
            _dispatcher.BeginInvoke(Run);
    }

    private void OnDwellActivated(string targetId)
    {
        if (targetId != ManualSelectionDwellTargetId) return;

        DispatchSafe(() =>
        {
            // The session can end between the detector raising TargetActivated and this
            // dispatched continuation running — OnManualSelectionEnded nulls _dwellDetector —
            // and completing into a torn-down ManualSelection would commit a click the user
            // already backed out of. Bail out instead.
            if (_dwellDetector == null) return;

            _logger.LogInformation("ManualSelection dwell completed");
            if (_anchor is { } anchor)
                LogLine(CommitLine(_lastFedTimestamp, anchor, _commitPoint ?? anchor, _commitCount));

            // Reset indicator
            _messenger.Send(new ResetManualSelectionIndicatorMessage());

            // Commit AT the anchor — the frozen point the ring shows — not at the running centroid
            // of the held samples: what the ring shows is what is clicked. The centroid is only in
            // the commit log line, to compare the two at the next sitting.
            _messenger.Send(new ManualSelectionDwellCompletedMessage(
                _anchor ?? _manualSelectionCenter!.Value));

            // Post-completion publish (spec D8): the cue fires only here — after the bail-out,
            // so a session torn down between the raise and this continuation publishes neither
            // the commit nor the cue (pinned by TornDownSession_PublishesNeitherCompletionNorCue).
            _messenger.Send(new DwellActivationMessage());
        }, "dwell-activated");
    }

    private void OnDwellProgress(string targetId, double progress)
    {
        if (targetId != ManualSelectionDwellTargetId) return;

        DispatchSafe(() => UpdateIndicatorProgress(progress), "dwell-progress");
    }

    private bool IsWithinManualSelectionBounds(PhysicalPoint gazePoint)
    {
        if (_manualSelectionCenter == null || _manualSelectionRadius == null) return false;

        var dx = gazePoint.X - _manualSelectionCenter.Value.X;
        var dy = gazePoint.Y - _manualSelectionCenter.Value.Y;
        return (dx * dx + dy * dy) <= (_manualSelectionRadius.Value * _manualSelectionRadius.Value);
    }

    /// <summary>Hold tolerance in physical pixels — the same DIP value the user set, converted to
    /// the space the gaze and the target live in. Magnification is deliberately absent.</summary>
    private double HoldRadiusPhysical => _settings.ManualSelection.HoldRadiusLogical * DpiHelper.ScaleX;

    private bool IsWithinHoldRadius(PhysicalPoint gazePoint)
    {
        if (_anchor is not { } anchor) return false;

        var dx = gazePoint.X - anchor.X;
        var dy = gazePoint.Y - anchor.Y;
        var radius = HoldRadiusPhysical;
        return (dx * dx + dy * dy) <= (radius * radius);
    }

    private void ResetDwell()
    {
        var hadAnchor = _anchor is not null;
        ClearCommitState();

        _dwellDetector?.ResetAllState();

        DispatchSafe(() => _messenger.Send(new ResetManualSelectionIndicatorMessage()), "indicator-reset");

        // Release the mark only when there was one; out-of-bounds samples arrive every frame once
        // the gaze has left the circle, and re-sending null would be noise on the messenger.
        if (hadAnchor)
            DispatchSafe(() => _messenger.Send(new ManualSelectionAnchorChangedMessage(null)), "anchor-released");
    }

    // ── The gated log lines (diagnostics.logRingEvents; invariant culture; physical view px) ──

    private void LogLine(string line)
    {
        if (_settings.Diagnostics.LogRingEvents)
            _logger.LogInformation("{MagnifierDwell:l}", line);
    }

    private void LogReset(string cause, PhysicalPoint? anchor, long? ts = null) =>
        LogLine(ResetLine(cause, ts, anchor));

    /// <summary>The seat: the device timestamp, ms since the session's first real sample, the anchor
    /// and how many of the window's samples seated it.</summary>
    internal static string SeatLine(long ts, long sinceStartMs, PhysicalPoint anchor, int k, int window) =>
        string.Format(CultureInfo.InvariantCulture,
            "{0} seat ts={1} ms={2} anchor=({3:0},{4:0}) k={5}/{6}",
            LogPrefix, ts, sinceStartMs, anchor.X, anchor.Y, k, window);

    /// <summary>A reset with its cause (bounds, hold or recapture) and the anchor it dropped
    /// (<c>none</c> when there was none). Bounds is logged once per excursion outside the rim.</summary>
    internal static string ResetLine(string cause, long? ts, PhysicalPoint? anchor) =>
        string.Format(CultureInfo.InvariantCulture,
            "{0} reset ({1}) ts={2} anchor={3}",
            LogPrefix, cause, ts is { } t ? t.ToString(CultureInfo.InvariantCulture) : "na", PointText(anchor));

    /// <summary>The detector's silence restart: the fill starts over, the anchor and ring stay.</summary>
    internal static string SilenceLine(long ts, long gapMs, PhysicalPoint? anchor) =>
        string.Format(CultureInfo.InvariantCulture,
            "{0} silence ts={1} gapMs={2} anchor={3}",
            LogPrefix, ts, gapMs, PointText(anchor));

    /// <summary>The commit: the anchor (the clicked view point), the held samples' centroid and their
    /// distance, and the number of samples in the centroid.</summary>
    internal static string CommitLine(long? ts, PhysicalPoint anchor, PhysicalPoint centroid, int n)
    {
        var dx = centroid.X - anchor.X;
        var dy = centroid.Y - anchor.Y;
        return string.Format(CultureInfo.InvariantCulture,
            "{0} commit ts={1} anchor=({2:0},{3:0}) centroid=({4:0},{5:0}) d={6:0} n={7}",
            LogPrefix, ts is { } t ? t.ToString(CultureInfo.InvariantCulture) : "na",
            anchor.X, anchor.Y, centroid.X, centroid.Y, Math.Sqrt(dx * dx + dy * dy), n);
    }

    /// <summary>The commit's real screen point, written where the view point is mapped
    /// (<c>OverlayMessageCoordinator</c>), gated the same way.</summary>
    internal static string CommitRealLine(PhysicalPoint view, PhysicalPoint real) =>
        string.Format(CultureInfo.InvariantCulture,
            "{0} commit real=({1:0},{2:0}) view=({3:0},{4:0})",
            LogPrefix, real.X, real.Y, view.X, view.Y);

    private static string PointText(PhysicalPoint? point) =>
        point is { } p
            ? string.Format(CultureInfo.InvariantCulture, "({0:0},{1:0})", p.X, p.Y)
            : "none";

    private void UpdateIndicatorPosition(PhysicalPoint gazePoint)
    {
        DispatchSafe(() => _messenger.Send(new UpdateManualSelectionIndicatorPositionMessage(gazePoint)), "indicator-position");
    }

    private void UpdateIndicatorProgress(double progress)
    {
        DispatchSafe(() => _messenger.Send(new UpdateManualSelectionIndicatorProgressMessage(progress)), "indicator-progress");
    }

    public void Dispose()
    {
        OnManualSelectionEnded();
        _messenger.UnregisterAll(this);
    }
}
