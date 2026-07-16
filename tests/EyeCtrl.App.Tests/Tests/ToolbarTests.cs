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
        AppFixture.SkipIfDisabled();
        _fixture = fixture;
    }

    [Fact]
    public async Task PauseButton_Dwell_HidesCursorCircle()
    {
        // Arrange — make sure we start in non-paused state
        await _fixture.EnsureAppUnpausedAsync();
        var cursorCircle = _fixture.FindWindow("CursorCircle_Window");
        Check.That(cursorCircle).IsNotNull();

        // Check initial state: cursor circle is visible
        var wasVisible = cursorCircle!.Properties.IsOffscreen.Value == false;
        Check.That(wasVisible).IsTrue();

        // Act — dwell on pause button
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Check.That(toolbar).IsNotNull();
        var pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        Check.That(pauseBtn).IsNotNull();

        await _fixture.SimulateDwellOn(pauseBtn!);

        // Assert — cursor circle should now be hidden
        await Task.Delay(200, TestContext.Current.CancellationToken); // Let UI update propagate
        cursorCircle = _fixture.FindWindow("CursorCircle_Window");

        // In paused state, the circle window is hidden (not visible)
        var isHidden = cursorCircle == null
            || cursorCircle.Properties.IsOffscreen.Value == true;
        Check.That(isHidden).IsTrue();

        // Cleanup — dwell on pause again to unpause
        toolbar = _fixture.FindWindow("Toolbar_Window");
        pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        if (pauseBtn != null)
            await _fixture.SimulateDwellOn(pauseBtn);

        // Move cursor away so it doesn't interfere with other tests
        _fixture.MoveCursorAway();
        await Task.Delay(500, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PauseButton_Dwell_TogglesBackToResume()
    {
        // Arrange — ensure clean state then pause
        await _fixture.EnsureAppUnpausedAsync();
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Check.That(toolbar).IsNotNull();
        var pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        Check.That(pauseBtn).IsNotNull();

        await _fixture.SimulateDwellOn(pauseBtn!);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Act — unpause
        toolbar = _fixture.FindWindow("Toolbar_Window");
        pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        Check.That(pauseBtn).IsNotNull();

        await _fixture.SimulateDwellOn(pauseBtn!);

        // After unpause, WPF calls Window.Show() which may take a render pass to
        // update the UIA automation peer from IsOffscreen=true to IsOffscreen=false.
        // Retry until the window is both found and actually visible.
        var cursorCircle = await _fixture.WaitForWindowVisibleAsync(
            "CursorCircle_Window", TimeSpan.FromSeconds(5));
        Check.That(cursorCircle.Properties.IsOffscreen.Value).IsFalse();

        _fixture.MoveCursorAway();
        await Task.Delay(500, TestContext.Current.CancellationToken);
    }
}
