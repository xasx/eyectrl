namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>Factory for creating the appropriate <see cref="IGazeFilter"/> based on cursor settings.</summary>
public static class GazeFilterFactory
{
    /// <summary>
    /// Creates an <see cref="IGazeFilter"/> instance based on the configured <see cref="CursorSettings.FilterMode"/>.
    /// </summary>
    /// <param name="settings">Cursor settings specifying filter mode and parameters.</param>
    /// <param name="effectiveSmoothingParameter">The EFFECTIVE value of the active filter's single
    /// adaptation-sensitive parameter (design §13.3 smoothing routing): <c>OneEuroMinCutoff</c> for
    /// <see cref="GazeFilterMode.OneEuro"/>, <c>SmoothingFactor</c> for
    /// <see cref="GazeFilterMode.Smoothed"/>/<see cref="GazeFilterMode.SmoothedWithSnapping"/>; ignored for
    /// <see cref="GazeFilterMode.Direct"/>, which has no smoothing parameter. <c>null</c> composes the filter
    /// from the baseline settings alone — the pre-adaptation behaviour. The caller owns clamping the value
    /// into the ACTIVE filter's validated range; this factory does not clamp.</param>
    /// <returns>A new gaze filter instance configured per the settings.</returns>
    public static IGazeFilter Create(CursorSettings settings, double? effectiveSmoothingParameter = null)
    {
        return settings.FilterMode switch
        {
            GazeFilterMode.Direct => new DirectFilter(),
            GazeFilterMode.Smoothed => new SmoothedFilter(effectiveSmoothingParameter ?? settings.SmoothingFactor),
            // Intentionally identical to Smoothed — see GazeFilterMode.SmoothedWithSnapping docs.
            GazeFilterMode.SmoothedWithSnapping => new SmoothedFilter(effectiveSmoothingParameter ?? settings.SmoothingFactor),
            GazeFilterMode.OneEuro => new OneEuroFilter(effectiveSmoothingParameter ?? settings.OneEuroMinCutoff, settings.OneEuroBetaPerDiagonal),
            _ => new OneEuroFilter(effectiveSmoothingParameter ?? settings.OneEuroMinCutoff, settings.OneEuroBetaPerDiagonal)
        };
    }
}
