namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// A pure forwarding <see cref="IFixationRule"/> over the PRODUCTION <see cref="FixationDetector"/>
/// (ticket 3, item 7). This is the measurement seam: the gates score the same class the app ships, so no
/// part of the valid-time ledger is re-implemented here — the adapter only maps four events and passes the
/// sample through. That is what makes a replay number a statement about production behaviour.
/// <para>
/// The onset timestamp comes from the sample being fed (the detector's event carries the point), exactly
/// as <see cref="LegacyFixationRule"/> reports it. Activation and release carry Core's own time and
/// credited age, so the harness never recomputes them.
/// </para>
/// </summary>
public sealed class ProductionFixationRule : IFixationRule
{
    private readonly FixationDetector _detector;
    private long _currentTs;

    /// <summary>Creates the forwarding adapter at a radius, with the pinned detection time, activation
    /// dwell and cooldown the production rule runs with.</summary>
    /// <param name="radiusPixels">Fixation radius in physical pixels.</param>
    /// <param name="fixationMs">Onset age (the pinned <c>DwellSettings</c> default).</param>
    /// <param name="activationDwellMs">Activation dwell after onset (the pinned <c>DwellSettings</c> default).</param>
    /// <param name="cooldownMs">Post-activation cooldown (the pinned <c>DwellSettings</c> default).</param>
    public ProductionFixationRule(
        double radiusPixels,
        int fixationMs = FixationReplayGates.FixationDetectionMs,
        int activationDwellMs = FixationReplayGates.ActivationButtonDwellMs,
        int cooldownMs = FixationReplayGates.ActivationCooldownMs)
    {
        _detector = new FixationDetector(radiusPixels, fixationMs)
        {
            ActivationDwellMs = activationDwellMs,
            CooldownMs = cooldownMs,
        };
        _detector.FixationStarted += point => FixationStarted?.Invoke(point, _currentTs);
        _detector.FixationActivated += activation => FixationActivated?.Invoke(activation.TimestampUs);
        _detector.FixationReleased += release => FixationReleased?.Invoke(
            new FixationRelease(release.LastCreditedUs, release.CreditedAgeUs, release.EndedByLeave));
    }

    /// <inheritdoc />
    public event Action<PhysicalPoint, long>? FixationStarted;

    /// <inheritdoc />
    public event Action<long>? FixationActivated;

    /// <inheritdoc />
    public event Action<FixationRelease>? FixationReleased;

    /// <inheritdoc />
    public void Update(PhysicalPoint point, long timestampUs, bool isValid)
    {
        _currentTs = timestampUs;
        _detector.Update(point, timestampUs, isValid);
    }

    /// <inheritdoc />
    public void CompleteStream() => _detector.CompleteStream();
}
