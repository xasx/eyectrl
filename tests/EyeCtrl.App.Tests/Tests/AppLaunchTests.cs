namespace EyeCtrl.App.Tests.Tests;

using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Verifies the app launches correctly in test mode and shows expected overlays.
/// </summary>
[Collection("App")]
[Trait("Category", "UI")]
public class AppLaunchTests
{
    private readonly AppFixture _fixture;

    public AppLaunchTests(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void App_LaunchesInTestMode_CursorCircleIsVisible()
    {
        var window = _fixture.FindWindow("CursorCircle_Window");

        Assert.NotNull(window);
        Assert.True(window!.Properties.IsOffscreen.Value == false,
            "Cursor circle window should be visible on screen");
    }

    [Fact]
    public void App_LaunchesInTestMode_ToolbarIsVisible()
    {
        var window = _fixture.FindWindow("Toolbar_Window");

        Assert.NotNull(window);
        Assert.True(window!.Properties.IsOffscreen.Value == false,
            "Toolbar window should be visible on screen");
    }

    [Fact]
    public void Toolbar_HasFourButtons()
    {
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);

        var buttons = _fixture.FindElements(toolbar!, "Toolbar_Button_");

        Assert.Equal(4, buttons.Length);
    }

    [Fact]
    public void Toolbar_ButtonsHaveCorrectIds()
    {
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);

        var expectedIds = new[]
        {
            "Toolbar_Button_Pause",
            "Toolbar_Button_Zoom",
            "Toolbar_Button_Keyboard",
            "Toolbar_Button_Settings"
        };

        foreach (var id in expectedIds)
        {
            var button = _fixture.FindElement(toolbar!, id);
            Assert.NotNull(button);
        }
    }

    [Fact]
    public void CursorCircle_HasMainEllipse()
    {
        var cursorCircle = _fixture.FindWindow("CursorCircle_Window");
        Assert.NotNull(cursorCircle);

        var ellipse = _fixture.FindElement(cursorCircle!, "CursorCircle_Ellipse_Main");
        Assert.NotNull(ellipse);
    }
}
