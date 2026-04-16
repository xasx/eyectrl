namespace EyeCtrl.Platform.Tests.Gaze;

using EyeCtrl.Core.Enums;
using EyeCtrl.Platform.Gaze;
using Microsoft.Extensions.Logging.Abstractions;

public class GazeProviderFactoryTests
{
    private readonly GazeProviderFactory _factory = new(
        NullLogger<GazeProviderFactory>.Instance,
        NullLoggerFactory.Instance);

    [Fact]
    public void Create_Mouse_ReturnsMouseGazeProvider()
    {
        var provider = _factory.Create(GazeProviderType.Mouse, 1920, 1080);
        Assert.IsType<MouseGazeProvider>(provider);
        provider.Dispose();
    }

    [Fact]
    public void Create_Auto_ReturnsValidProvider()
    {
        // Auto returns StreamEngine if Tobii hardware is available, otherwise Mouse
        var provider = _factory.Create(GazeProviderType.Auto, 1920, 1080);
#if TOBII_AVAILABLE
        Assert.True(provider is TobiiStreamEngineProvider or MouseGazeProvider);
#else
        Assert.IsType<MouseGazeProvider>(provider);
#endif
        provider.Dispose();
    }

    [Fact]
    public void Create_StreamEngine_ReturnsProviderOrThrows()
    {
#if TOBII_AVAILABLE
        // Succeeds if Tobii hardware/DLLs are available, throws otherwise
        try
        {
            var provider = _factory.Create(GazeProviderType.StreamEngine, 1920, 1080);
            Assert.IsType<TobiiStreamEngineProvider>(provider);
            provider.Dispose();
        }
        catch (Exception ex)
        {
            // Expected when no Tobii hardware is present
            // Should throw a specific exception (not ArgumentOutOfRangeException)
            Assert.IsNotType<ArgumentOutOfRangeException>(ex);
        }
#else
        // When TOBII_AVAILABLE is not defined, should throw PlatformNotSupportedException
        var ex = Assert.Throws<PlatformNotSupportedException>(
            () => _factory.Create(GazeProviderType.StreamEngine, 1920, 1080));
        Assert.Contains("Tobii Stream Engine is not available", ex.Message);
#endif
    }
}
