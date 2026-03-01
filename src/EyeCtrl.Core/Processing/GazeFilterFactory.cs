namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public static class GazeFilterFactory
{
    public static IGazeFilter Create(CursorSettings settings)
    {
        return settings.FilterMode switch
        {
            GazeFilterMode.Direct => new DirectFilter(),
            GazeFilterMode.Smoothed => new SmoothedFilter(settings.SmoothingFactor),
            GazeFilterMode.SmoothedWithSnapping => new SmoothedFilter(settings.SmoothingFactor),
            GazeFilterMode.OneEuro => new OneEuroFilter(settings.OneEuroMinCutoff, settings.OneEuroBeta),
            _ => new OneEuroFilter(settings.OneEuroMinCutoff, settings.OneEuroBeta)
        };
    }
}
