namespace EyeCtrl.App.Tests.Tests;

using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Tests toolbar interactions: pause toggle, button state changes.
/// </summary>
[Collection("App")]
[Trait("Category", "UI")]
public class ToolbarTests
{
    private readonly AppFixture _fixture;

    public ToolbarTests(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PauseButton_Dwell_HidesCursorCircle()
    {
        // Arrange — make sure we start in non-paused state
        await _fixture.EnsureAppUnpausedAsync();
        var cursorCircle = _fixture.FindWindow("CursorCircle_Window");
        Assert.NotNull(cursorCircle);

        // Check initial state: cursor circle is visible
        var wasVisible = cursorCircle!.Properties.IsOffscreen.Value == false;
        Assert.True(wasVisible, "Cursor circle should be visible initially");

        // Act — dwell on pause button
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        Assert.NotNull(pauseBtn);

        await _fixture.SimulateDwellOn(pauseBtn!);

        // Assert — cursor circle should now be hidden
        await Task.Delay(200); // Let UI update propagate
        cursorCircle = _fixture.FindWindow("CursorCircle_Window");

        // In paused state, the circle window is hidden (not visible)
        var isHidden = cursorCircle == null
            || cursorCircle.Properties.IsOffscreen.Value == true;
        Assert.True(isHidden, "Cursor circle should be hidden after pause");

        // Cleanup — dwell on pause again to unpause
        toolbar = _fixture.FindWindow("Toolbar_Window");
        pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        if (pauseBtn != null)
            await _fixture.SimulateDwellOn(pauseBtn);

        // Move cursor away so it doesn't interfere with other tests
        _fixture.MoveCursorAway();
        await Task.Delay(500);
    }

    [Fact]
    public async Task PauseButton_Dwell_TogglesBackToResume()
    {
        // Arrange — ensure clean state then pause
        await _fixture.EnsureAppUnpausedAsync();
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        Assert.NotNull(pauseBtn);

        await _fixture.SimulateDwellOn(pauseBtn!);
        await Task.Delay(200);

        // Act — unpause
        toolbar = _fixture.FindWindow("Toolbar_Window");
        pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        Assert.NotNull(pauseBtn);

        await _fixture.SimulateDwellOn(pauseBtn!);

        // After unpause, WPF calls Window.Show() which may take a render pass to
        // update the UIA automation peer from IsOffscreen=true to IsOffscreen=false.
        // Retry until the window is both found and actually visible.
        var cursorCircle = await _fixture.WaitForWindowVisibleAsync(
            "CursorCircle_Window", TimeSpan.FromSeconds(5));
        Assert.True(cursorCircle.Properties.IsOffscreen.Value == false,
            "Cursor circle should be visible after unpause");

        _fixture.MoveCursorAway();
        await Task.Delay(500);
    }
}
