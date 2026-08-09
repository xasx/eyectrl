namespace EyeCtrl.Platform.Tests.Gaze;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Platform.Gaze;
using Microsoft.Extensions.Logging.Abstractions;
using NFluent;
using Xunit;

public class GazeProviderTypeTests
{
    private static readonly GazeProviderFactory Factory = new(
        NullLogger<GazeProviderFactory>.Instance,
        NullLoggerFactory.Instance);

    [Fact]
    public void GetProviderType_MouseProvider_ReturnsMouse()
    {
        var provider = new MouseGazeProvider(logger: NullLogger<MouseGazeProvider>.Instance);

        Check.That(GazeProviderFactory.GetProviderType(provider)).IsEqualTo(GazeProviderType.Mouse);
    }

    [Fact]
    public void Create_Mouse_ReturnsMouseGazeProvider()
    {
        IGazeProvider provider = Factory.Create(GazeProviderType.Mouse, 1920, 1080);

        Check.That(provider).IsNotNull();
        Check.That(provider).IsInstanceOf<MouseGazeProvider>();
        provider.Dispose();
    }

#if TOBII_AVAILABLE
    [Fact]
    public void Create_Auto_WithTobiiSdk_ReturnsValidProvider()
    {
        IGazeProvider provider = Factory.Create(GazeProviderType.Auto, 1920, 1080);
        var type = GazeProviderFactory.GetProviderType(provider);

        Check.That(type is GazeProviderType.StreamEngine or GazeProviderType.Mouse).IsTrue();
        provider.Dispose();
    }
#else
    [Fact]
    public void Create_StreamEngine_WithoutTobiiSdk_ThrowsPlatformNotSupportedException()
    {
        PlatformNotSupportedException? ex = null;
        try
        {
            Factory.Create(GazeProviderType.StreamEngine, 1920, 1080);
        }
        catch (PlatformNotSupportedException e)
        {
            ex = e;
        }

        Check.That(ex).IsNotNull();
    }

    [Fact]
    public void Create_Auto_WithoutTobiiSdk_ReturnsMouseProvider()
    {
        IGazeProvider provider = Factory.Create(GazeProviderType.Auto, 1920, 1080);

        Check.That(provider).IsInstanceOf<MouseGazeProvider>();
        provider.Dispose();
    }
#endif
}
