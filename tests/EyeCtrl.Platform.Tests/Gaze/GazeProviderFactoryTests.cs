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
        var provider = _factory.Create(GazeProviderType.Auto, 1920, 1080);
        var type = GazeProviderFactory.GetProviderType(provider);
        Assert.True(type is GazeProviderType.StreamEngine or GazeProviderType.Mouse);
        provider.Dispose();
    }

#if TOBII_AVAILABLE
    [Fact]
    public void Create_StreamEngine_SucceedsOrThrowsNoDevice()
    {
        // SDK DLLs are present, but hardware may not be connected
        var exception = Record.Exception(() =>
        {
            var provider = _factory.Create(GazeProviderType.StreamEngine, 1920, 1080);
            Assert.Equal(GazeProviderType.StreamEngine, GazeProviderFactory.GetProviderType(provider));
            provider.Dispose();
        });

        if (exception is not null)
        {
            // Only "no device found" is acceptable here
            Assert.IsType<InvalidOperationException>(exception);
            Assert.Contains("No Tobii device found", exception.Message);
        }
    }
#else
    [Fact]
    public void Create_StreamEngine_ThrowsPlatformNotSupported()
    {
        Assert.Throws<PlatformNotSupportedException>(() =>
            _factory.Create(GazeProviderType.StreamEngine, 1920, 1080));
    }
#endif
}
