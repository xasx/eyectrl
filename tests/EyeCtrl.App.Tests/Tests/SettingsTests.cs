namespace EyeCtrl.App.Tests.Tests;

using System;
using System.Linq;
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
        AppFixture.SkipIfDisabled();
        _fixture = fixture;
    }

    [Fact]
    public async Task SettingsButton_Dwell_OpensSettingsWindow()
    {
        // Arrange
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Check.That(toolbar).IsNotNull();
        var settingsBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Settings");
        Check.That(settingsBtn).IsNotNull();

        // Act — dwell on settings button
        await _fixture.SimulateDwellOn(settingsBtn!);
        await Task.Delay(500, TestContext.Current.CancellationToken); // Wait for window to appear

        // Assert — settings window should appear
        var settings = _fixture.FindWindow("Settings_Window");
        Check.That(settings).IsNotNull();

        _fixture.MoveCursorAway();
    }

    /// <summary>
    /// The expected ids are kept in sync with <c>SettingsWindow._tabDefs</c> by
    /// <c>SettingsTabDriftTests</c>, which runs headlessly. This test only runs when UI tests are
    /// opted in, so on its own it cannot be trusted to notice product drift — it previously expected
    /// eight tabs including <c>ManualSelection</c> and <c>Theme</c>, neither of which still exists.
    /// </summary>
    [Fact]
    public async Task SettingsWindow_RendersExactlyTheExpectedTabs()
    {
        // Ensure settings is open
        var settings = _fixture.FindWindow("Settings_Window");
        if (settings == null)
        {
            var toolbar = _fixture.FindWindow("Toolbar_Window");
            var settingsBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Settings");
            await _fixture.SimulateDwellOn(settingsBtn!);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            settings = _fixture.FindWindow("Settings_Window");
        }
        Check.That(settings).IsNotNull();

        var expectedTabs = new[]
        {
            "Settings_Tab_Cursor",
            "Settings_Tab_Dwell",
            "Settings_Tab_Selection",
            "Settings_Tab_Actions",
            "Settings_Tab_Layout",
            "Settings_Tab_Keyboard",
            "Settings_Tab_Models",
            "Settings_Tab_Toolbar",
            "Settings_Tab_Calibration",
            "Settings_Tab_General",
            "Settings_Tab_QuickActions"
        };

        var actualTabs = _fixture.FindElements(settings!, "Settings_Tab_")
            .Select(e => e.Properties.AutomationId.Value)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Check.WithCustomMessage(
                "Rendered settings tabs drifted from the expected set. Rendered: "
                + string.Join(", ", actualTabs))
            .That(actualTabs)
            .ContainsExactly(expectedTabs.OrderBy(id => id, StringComparer.Ordinal).ToArray());

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
            await Task.Delay(500, TestContext.Current.CancellationToken);
            settings = _fixture.FindWindow("Settings_Window");
        }
        Check.That(settings).IsNotNull();

        var contentPanel = _fixture.FindElement(settings!, "Settings_Panel_Content");
        Check.That(contentPanel).IsNotNull();

        _fixture.MoveCursorAway();
    }
}
