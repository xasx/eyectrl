namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// The output of one struggle-signal scorer (design §6.3/§13.2): a normalized struggle
/// <paramref name="Score"/> with an evidence <paramref name="Confidence"/> and whether the signal's
/// confidence gate is met. The controller (plan 1b-④) acts on a signal's failure mode only when
/// <paramref name="GatePassed"/> is true — there is no single mixed gate across signals.
/// </summary>
/// <param name="Score">Struggle score in [0, 1] (0 = no struggle evidence).</param>
/// <param name="Confidence">Evidence confidence in [0, 1], ramping to 1 as evidence reaches the gate.</param>
/// <param name="GatePassed">True when enough evidence has accrued for the controller to act on this signal.</param>
public readonly record struct SignalScore(double Score, double Confidence, bool GatePassed)
{
    /// <summary>A zero score with no confidence and an unmet gate — returned when the evidence window is empty.</summary>
    public static SignalScore None => new(0.0, 0.0, false);
}
