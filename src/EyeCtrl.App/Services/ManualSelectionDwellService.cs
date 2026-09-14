namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
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
    // target radius was unreachable and every sample counted as inside). The commit point is the
    // running centroid of the held samples, which recovers accuracy under noise.
    private readonly Queue<PhysicalPoint> _stabilitySamples = new();
    private PhysicalPoint? _anchor;
    private PhysicalPoint? _commitPoint;
    private double _commitSumX;
    private double _commitSumY;
    private int _commitCount;
    private int _consecutiveOutOfHoldSamples;

    /// <summary>
    /// Onset window as a fraction of the hold radius: the seating cluster must be this tight
    /// before the anchor seats. Tied to the hold radius so the one strictness knob
    /// (<see cref="ManualSelectionSettings.HoldRadiusLogical"/>) loosens seating and holding together.
    /// </summary>
    private const double OnsetRadiusHoldFraction = 0.5;

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
        ClearCommitState();

        _dwellDetector.ResetAllState();
        _gracePeriodActive = false;
        _manualSelectionStartTimestamp = 0;

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
        // at a held timestamp — and the indicator simply stays where it was, since the point is
        // unchanged.
        if (message.IsInGap)
            return;

        var pt = message.Position;
        var ts = message.TimestampMicroseconds;

        // 1. Update indicator position (even during grace period)
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
            ResetDwell();
            return;
        }

        // 4. Commit rule: the anchor must exist before dwell can accumulate, and once it exists it
        //    is held. Only samples within the hold radius are fed to the detector; an isolated
        //    out-of-hold sample is skipped rather than treated as gaze-left.
        if (_anchor is null)
        {
            if (!TrySeatAnchor(pt))
                return;
        }
        else if (!IsWithinHoldRadius(pt))
        {
            _consecutiveOutOfHoldSamples++;
            if (_consecutiveOutOfHoldSamples > _settings.ManualSelection.HoldOutlierSkipLimit)
            {
                // Not an isolated outlier: the user moved. Drop the anchor, reset the dwell and
                // start looking for a new stability onset from this sample.
                ResetDwell();
                TrySeatAnchor(pt);
            }
            return;
        }
        else
        {
            _consecutiveOutOfHoldSamples = 0;
            AccumulateCommitPoint(pt);
        }

        // 5. Update dwell detector
        _dwellDetector.Update(pt, ts);
    }

    /// <summary>
    /// Seats the dwell anchor on stability onset: at least
    /// <see cref="ManualSelectionSettings.StabilitySampleCount"/> of the last
    /// <see cref="ManualSelectionSettings.StabilityWindowSize"/> samples must lie within the onset
    /// window of the newest sample. The anchor is their centroid, and it also seeds the commit
    /// centroid, so seating error is ~sigma/sqrt(K).
    /// </summary>
    private bool TrySeatAnchor(PhysicalPoint sample)
    {
        var sampleCount = _settings.ManualSelection.StabilitySampleCount;
        var windowSize = Math.Max(_settings.ManualSelection.StabilityWindowSize, sampleCount);

        _stabilitySamples.Enqueue(sample);
        while (_stabilitySamples.Count > windowSize)
            _stabilitySamples.Dequeue();

        var onsetRadius = HoldRadiusPhysical * OnsetRadiusHoldFraction;
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

            // Reset indicator
            _messenger.Send(new ResetManualSelectionIndicatorMessage());

            // Commit at the running centroid of the held samples, not at the live gaze point: the
            // centroid averages out the noise that a single sample would commit, which is what
            // recovers accuracy now that the target no longer tracks the gaze.
            _messenger.Send(new ManualSelectionDwellCompletedMessage(
                _commitPoint ?? _manualSelectionCenter!.Value));
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
