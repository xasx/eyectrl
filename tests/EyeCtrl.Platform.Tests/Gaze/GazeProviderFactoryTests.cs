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
        Check.That(provider).IsInstanceOf<MouseGazeProvider>();
        provider.Dispose();
    }

    [Fact]
    public void Create_Auto_ReturnsValidProvider()
    {
        var provider = _factory.Create(GazeProviderType.Auto, 1920, 1080);
        var type = GazeProviderFactory.GetProviderType(provider);
        Check.That(type is GazeProviderType.StreamEngine or GazeProviderType.Mouse).IsTrue();
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
            Check.That(GazeProviderFactory.GetProviderType(provider)).IsEqualTo(GazeProviderType.StreamEngine);
            provider.Dispose();
        });

        if (exception is not null)
        {
            // Only "no device found" is acceptable here
            Check.That(exception).IsInstanceOf<InvalidOperationException>();
            Check.That(exception.Message).Contains("No Tobii device found");
        }
    }
#else
    [Fact]
    public void Create_StreamEngine_ThrowsPlatformNotSupported()
    {
        Check.ThatCode(() =>
            _factory.Create(GazeProviderType.StreamEngine, 1920, 1080)).Throws<PlatformNotSupportedException>();
    }
#endif
}
