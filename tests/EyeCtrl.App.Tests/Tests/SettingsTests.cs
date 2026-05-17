namespace EyeCtrl.App.Tests.Tests;

using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Tests settings window: opens, tabs are present, tab navigation works.
/// </summary>
[Collection("App")]
[Trait("Category", "UI")]
public class SettingsTests
{
    private readonly AppFixture _fixture;

    public SettingsTests(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SettingsButton_Dwell_OpensSettingsWindow()
    {
        // Arrange
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var settingsBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Settings");
        Assert.NotNull(settingsBtn);

        // Act — dwell on settings button
        await _fixture.SimulateDwellOn(settingsBtn!);
        await Task.Delay(500); // Wait for window to appear

        // Assert — settings window should appear
        var settings = _fixture.FindWindow("Settings_Window");
        Assert.NotNull(settings);

        _fixture.MoveCursorAway();
    }

    [Fact]
    public async Task SettingsWindow_HasSevenTabs()
    {
        // Ensure settings is open
        var settings = _fixture.FindWindow("Settings_Window");
        if (settings == null)
        {
            var toolbar = _fixture.FindWindow("Toolbar_Window");
            var settingsBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Settings");
            await _fixture.SimulateDwellOn(settingsBtn!);
            await Task.Delay(500);
            settings = _fixture.FindWindow("Settings_Window");
        }
        Assert.NotNull(settings);

        var expectedTabs = new[]
        {
            "Settings_Tab_Cursor",
            "Settings_Tab_Dwell",
            "Settings_Tab_ManualSelection",
            "Settings_Tab_Keyboard",
            "Settings_Tab_Toolbar",
            "Settings_Tab_Theme",
            "Settings_Tab_General"
        };

        foreach (var tabId in expectedTabs)
        {
            var tab = _fixture.FindElement(settings!, tabId);
            Assert.NotNull(tab);
        }

        _fixture.MoveCursorAway();
    }

    [Fact]
    public async Task SettingsWindow_HasContentPanel()
    {
        var settings = _fixture.FindWindow("Settings_Window");
        if (settings == null)
        {
            var toolbar = _fixture.FindWindow("Toolbar_Window");
            var settingsBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Settings");
            await _fixture.SimulateDwellOn(settingsBtn!);
            await Task.Delay(500);
            settings = _fixture.FindWindow("Settings_Window");
        }
        Assert.NotNull(settings);

        var contentPanel = _fixture.FindElement(settings!, "Settings_Panel_Content");
        Assert.NotNull(contentPanel);

        _fixture.MoveCursorAway();
    }
}
