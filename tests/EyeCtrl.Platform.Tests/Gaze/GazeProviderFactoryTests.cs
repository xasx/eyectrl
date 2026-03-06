namespace EyeCtrl.Platform.Tests.Gaze;

using EyeCtrl.Core.Enums;
using EyeCtrl.Platform.Gaze;

public class GazeProviderFactoryTests
{
    private readonly GazeProviderFactory _factory = new();

    [Fact]
    public void Create_Mouse_ReturnsMouseGazeProvider()
    {
        var provider = _factory.Create(GazeProviderType.Mouse, 1920, 1080);
        Assert.IsType<MouseGazeProvider>(provider);
        provider.Dispose();
    }

    [Fact]
    public void Create_Auto_FallsBackToMouse_WhenNoTobiiHardware()
    {
        var provider = _factory.Create(GazeProviderType.Auto, 1920, 1080);
        Assert.IsType<MouseGazeProvider>(provider);
        provider.Dispose();
    }

    [Fact]
    public void Create_InteractionLib_Throws_WhenNoTobiiHardware()
    {
        Assert.ThrowsAny<Exception>(() =>
            _factory.Create(GazeProviderType.InteractionLib, 1920, 1080));
    }

    [Fact]
    public void Create_StreamEngine_Throws_WhenNoTobiiHardware()
    {
        Assert.ThrowsAny<Exception>(() =>
            _factory.Create(GazeProviderType.StreamEngine, 1920, 1080));
    }
}
