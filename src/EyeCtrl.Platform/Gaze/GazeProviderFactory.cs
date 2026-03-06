namespace EyeCtrl.Platform.Gaze;

using System.Diagnostics;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;

public class GazeProviderFactory
{
    /// <summary>
    /// Creates a gaze provider for the requested type.
    /// For Auto: tries InteractionLib -> StreamEngine -> Mouse, returning the first that succeeds.
    /// For explicit types: creates that provider and throws if unavailable.
    /// </summary>
    public IGazeProvider Create(GazeProviderType type, double screenWidth, double screenHeight)
    {
        return type switch
        {
            GazeProviderType.Mouse => new MouseGazeProvider(),
            GazeProviderType.InteractionLib => new TobiiInteractionLibProvider(screenWidth, screenHeight),
            GazeProviderType.StreamEngine => new TobiiStreamEngineProvider(screenWidth, screenHeight),
            GazeProviderType.Auto => CreateAuto(screenWidth, screenHeight),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    /// <summary>
    /// Returns the GazeProviderType that describes the given provider instance.
    /// Used by the toolbar to show which engine is active.
    /// </summary>
    public static GazeProviderType GetProviderType(IGazeProvider provider) => provider switch
    {
        TobiiInteractionLibProvider => GazeProviderType.InteractionLib,
        TobiiStreamEngineProvider => GazeProviderType.StreamEngine,
        _ => GazeProviderType.Mouse
    };

    private IGazeProvider CreateAuto(double screenWidth, double screenHeight)
    {
        // Try InteractionLib first
        try
        {
            var provider = new TobiiInteractionLibProvider(screenWidth, screenHeight);
            Trace.TraceInformation("[GazeProviderFactory] Using Tobii Interaction Library");
            return provider;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"[GazeProviderFactory] InteractionLib unavailable: {ex.Message}");
        }

        // Try StreamEngine
        try
        {
            var provider = new TobiiStreamEngineProvider(screenWidth, screenHeight);
            Trace.TraceInformation("[GazeProviderFactory] Using Tobii Stream Engine");
            return provider;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"[GazeProviderFactory] StreamEngine unavailable: {ex.Message}");
        }

        // Fall back to mouse
        Trace.TraceInformation("[GazeProviderFactory] Falling back to Mouse simulation");
        return new MouseGazeProvider();
    }
}
