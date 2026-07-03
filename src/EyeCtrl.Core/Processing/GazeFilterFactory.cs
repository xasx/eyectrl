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
    /// <returns>A new gaze filter instance configured per the settings.</returns>
    public static IGazeFilter Create(CursorSettings settings)
    {
        return settings.FilterMode switch
        {
            GazeFilterMode.Direct => new DirectFilter(),
            GazeFilterMode.Smoothed => new SmoothedFilter(settings.SmoothingFactor),
            // Intentionally identical to Smoothed — see GazeFilterMode.SmoothedWithSnapping docs.
            GazeFilterMode.SmoothedWithSnapping => new SmoothedFilter(settings.SmoothingFactor),
            GazeFilterMode.OneEuro => new OneEuroFilter(settings.OneEuroMinCutoff, settings.OneEuroBeta),
            _ => new OneEuroFilter(settings.OneEuroMinCutoff, settings.OneEuroBeta)
        };
    }
}
