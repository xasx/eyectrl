namespace EyeCtrl.App.Tests.Tests;

using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Tests keyboard overlay: opens via toolbar, shows keys.
/// </summary>
[Collection("App")]
[Trait("Category", "UI")]
public class KeyboardTests
{
    private readonly AppFixture _fixture;

    public KeyboardTests(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task KeyboardButton_Dwell_OpensKeyboard()
    {
        // Arrange
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        Assert.NotNull(kbBtn);

        // Act — dwell on keyboard button
        await _fixture.SimulateDwellOn(kbBtn!);
        await Task.Delay(500);

        // Assert — keyboard window should appear
        var keyboard = _fixture.FindWindow("Keyboard_Window");
        Assert.NotNull(keyboard);
        Assert.True(keyboard!.Properties.IsOffscreen.Value == false,
            "Keyboard window should be visible");

        // Cleanup — toggle keyboard off
        toolbar = _fixture.FindWindow("Toolbar_Window");
        kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        if (kbBtn != null)
            await _fixture.SimulateDwellOn(kbBtn);

        _fixture.MoveCursorAway();
        await Task.Delay(500);
    }

    [Fact]
    public async Task Keyboard_HasKeys()
    {
        // Open keyboard
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        Assert.NotNull(kbBtn);
        await _fixture.SimulateDwellOn(kbBtn!);
        await Task.Delay(500);

        // Assert — should have key elements
        var keyboard = _fixture.FindWindow("Keyboard_Window");
        Assert.NotNull(keyboard);

        var keys = _fixture.FindElements(keyboard!, "Keyboard_Key_");
        Assert.True(keys.Length > 0, "Keyboard should have at least one key");

        // Cleanup — toggle keyboard off
        toolbar = _fixture.FindWindow("Toolbar_Window");
        kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        if (kbBtn != null)
            await _fixture.SimulateDwellOn(kbBtn);

        _fixture.MoveCursorAway();
        await Task.Delay(500);
    }
}
