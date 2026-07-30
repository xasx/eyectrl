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
    public void Create_StreamEngine_DoesNotThrow_RegardlessOfDevicePresence()
    {
        // After the resilient-connection refactor, constructing a StreamEngine
        // provider never throws — with or without hardware.  Start retries in
        // the background poll loop.
        var provider = _factory.Create(GazeProviderType.StreamEngine, 1920, 1080);
        Check.That(provider).IsNotNull();
        Check.That(GazeProviderFactory.GetProviderType(provider)).IsEqualTo(GazeProviderType.StreamEngine);
        provider.Dispose();
    }

    [Fact]
    public void Create_StreamEngine_StartStopDoesNotThrow_RegardlessOfDevicePresence()
    {
        var provider = _factory.Create(GazeProviderType.StreamEngine, 1920, 1080);
        Check.That(provider).IsNotNull();

        // Start attempts connection and falls back to poll-loop retry; Stop
        // joins the poll thread and tears down device + API.  Neither throws
        // whether or not a tracker is attached — that is the whole contract
        // here.
        //
        // Deliberately NOT asserting IsConnected: Start() connects the device
        // synchronously via TryFullConnect() but only the poll thread sets
        // IsConnected, after its first successful callback round-trip.  On a
        // machine with a real tracker attached that lands within milliseconds,
        // so any assertion on IsConnected is a race against thread scheduling.
        Check.ThatCode(() =>
        {
            provider.Start();
            provider.Stop();
        }).DoesNotThrow();

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
