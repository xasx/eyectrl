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
    public void Create_StreamEngine_DoesNotThrow_WhenNoDeviceConnected()
    {
        // After the resilient-connection refactor, constructing a StreamEngine
        // provider never throws — even with no hardware.  Start retries in the
        // background poll loop.
        var provider = _factory.Create(GazeProviderType.StreamEngine, 1920, 1080);
        Check.That(provider).IsNotNull();
        Check.That(GazeProviderFactory.GetProviderType(provider)).IsEqualTo(GazeProviderType.StreamEngine);
        provider.Dispose();
    }

    [Fact]
    public void Create_StreamEngine_StartDoesNotThrow_WhenNoDeviceConnected()
    {
        var provider = _factory.Create(GazeProviderType.StreamEngine, 1920, 1080);
        Check.That(provider).IsNotNull();

        // Start attempts connection and falls back to poll-loop retry — no throw
        provider.Start();
        Check.That(provider.IsConnected).IsFalse(); // hardware not present

        provider.Stop();
        provider.Dispose();
    }
#else
    [Fact]
    public void Create_StreamEngine_ThrowsPlatformNotSupported()
    {
        Check.ThatCode(() =>
            _factory.Create(GazeProviderType.StreamEngine, 1920, 1080)).Throws<PlatformNotSupportedException>();
    }

    [Fact]
    public void Create_InteractionLib_FallsBackToMouse()
    {
        // InteractionLib is obsolete — in non-TOBII builds the factory falls
        // back to Auto, which falls back to Mouse.
        var provider = _factory.Create(GazeProviderType.InteractionLib, 1920, 1080);
        Check.That(GazeProviderFactory.GetProviderType(provider)).IsEqualTo(GazeProviderType.Mouse);
        provider.Dispose();
    }
#endif
}
