namespace EyeCtrl.Platform.Gaze;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using Microsoft.Extensions.Logging;

public class GazeProviderFactory
{
    private readonly ILogger<GazeProviderFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public GazeProviderFactory(ILogger<GazeProviderFactory> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public IGazeProvider Create(GazeProviderType type, double screenWidth, double screenHeight)
    {
        return type switch
        {
            GazeProviderType.Mouse => new MouseGazeProvider(),
#if TOBII_AVAILABLE
            GazeProviderType.StreamEngine => new TobiiStreamEngineProvider(screenWidth, screenHeight,
                _loggerFactory.CreateLogger<TobiiStreamEngineProvider>()),
#pragma warning disable CS0618 // InteractionLib is obsolete
            GazeProviderType.InteractionLib => new TobiiStreamEngineProvider(screenWidth, screenHeight,
                _loggerFactory.CreateLogger<TobiiStreamEngineProvider>()),
#pragma warning restore CS0618
#else
            GazeProviderType.StreamEngine => throw new PlatformNotSupportedException(
                "Tobii Stream Engine is not available on this platform. The Tobii SDK was not found during build."),
#pragma warning disable CS0618
            GazeProviderType.InteractionLib => CreateAuto(screenWidth, screenHeight),
#pragma warning restore CS0618
#endif
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
#if TOBII_AVAILABLE
        TobiiStreamEngineProvider => GazeProviderType.StreamEngine,
#endif
        _ => GazeProviderType.Mouse
    };

    private IGazeProvider CreateAuto(double screenWidth, double screenHeight)
    {
#if TOBII_AVAILABLE
        try
        {
            var provider = new TobiiStreamEngineProvider(screenWidth, screenHeight,
                _loggerFactory.CreateLogger<TobiiStreamEngineProvider>());
            _logger.LogInformation("Using Tobii Stream Engine");
            return provider;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("StreamEngine unavailable: {Message}", ex.Message);
        }
#endif

        _logger.LogInformation("Falling back to Mouse simulation");
        return new MouseGazeProvider();
    }
}
