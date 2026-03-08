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
        // Arrange — ensure keyboard starts closed
        await _fixture.EnsureKeyboardClosedAsync();
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        Assert.NotNull(kbBtn);

        // Act — dwell on keyboard button
        await _fixture.SimulateDwellOn(kbBtn!);

        // Wait for keyboard window to appear in the UIA tree (retries until visible).
        var keyboard = await _fixture.WaitForWindowAsync(
            "Keyboard_Window", TimeSpan.FromSeconds(5));
        Assert.True(keyboard.Properties.IsOffscreen.Value == false,
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
    public async Task ClickSoundToggle_ExistsEnabledByDefault_AndTogglesOnDwell()
    {
        // Arrange — open settings on the Keyboard tab
        var settings = _fixture.FindWindow("Settings_Window");
        if (settings == null)
        {
            var toolbar = _fixture.FindWindow("Toolbar_Window");
            var settingsBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Settings");
            Assert.NotNull(settingsBtn);
            await _fixture.SimulateDwellOn(settingsBtn!);
            await Task.Delay(500);
            settings = _fixture.FindWindow("Settings_Window");
        }
        Assert.NotNull(settings);

        var keyboardTab = _fixture.FindElement(settings!, "Settings_Tab_Keyboard");
        Assert.NotNull(keyboardTab);
        await _fixture.SimulateDwellOn(keyboardTab!);
        await Task.Delay(500); // wait for tab content to rebuild

        // Find the Click Sound toggle — AutomationId set by AddToggle
        settings = _fixture.FindWindow("Settings_Window");
        var toggle = await _fixture.WaitForElementAsync(settings!, "Settings_Toggle_k_sound");
        var enabledText = toggle.Properties.Name.Value;
        Assert.Contains("Click Sound", enabledText);
        Assert.StartsWith("\u2611", enabledText); // ☑ = enabled by default

        // Act — dwell to disable
        await _fixture.SimulateDwellOn(toggle);
        await Task.Delay(300);

        // Assert — toggle now shows disabled
        settings = _fixture.FindWindow("Settings_Window");
        toggle = _fixture.FindElement(settings!, "Settings_Toggle_k_sound")!;
        Assert.StartsWith("\u2610", toggle.Properties.Name.Value); // ☐ = disabled

        // Cleanup — restore to enabled
        await _fixture.SimulateDwellOn(toggle);
        await Task.Delay(300);
        _fixture.MoveCursorAway();
    }

    [Fact]
    public async Task Keyboard_HasKeys()
    {
        // Arrange — ensure keyboard starts closed so we can open it fresh
        await _fixture.EnsureKeyboardClosedAsync();
        // Open keyboard
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        Assert.NotNull(kbBtn);
        await _fixture.SimulateDwellOn(kbBtn!);

        // Wait for keyboard window to appear in the UIA tree.
        var keyboard = await _fixture.WaitForWindowAsync(
            "Keyboard_Window", TimeSpan.FromSeconds(5));

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
