namespace EyeCtrl.Core.Settings;

/// <summary>
/// How a rest becomes an activation (rest-activation plan, decided by the owner on 2026-11-08).
/// </summary>
public enum ActivationStyle
{
    /// <summary>The default: after the settle the cursor ring stays at the resting point and fills
    /// for <see cref="DwellSettings.RestFillMs"/>; full = activation, looking away = cancel. No
    /// second target.</summary>
    RestFill,

    /// <summary>The previous flow, kept as a fallback („Knopf neben dem Ring"): a button is offered
    /// beside the ring and dwelt for <see cref="DwellSettings.ActivationButtonDwellMs"/>.</summary>
    OfferButton,
}
