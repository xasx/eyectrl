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
        AppFixture.SkipIfDisabled();
        _fixture = fixture;
    }

    [Fact]
    public async Task KeyboardButton_Dwell_OpensKeyboard()
    {
        // Arrange — ensure keyboard starts closed
        await _fixture.EnsureKeyboardClosedAsync();
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Check.That(toolbar).IsNotNull();
        var kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        Check.That(kbBtn).IsNotNull();

        // Act — dwell on keyboard button
        await _fixture.SimulateDwellOn(kbBtn!);

        // Wait for keyboard window to appear in the UIA tree (retries until visible).
        var keyboard = await _fixture.WaitForWindowAsync(
            "Keyboard_Window", TimeSpan.FromSeconds(5));
        Check.That(keyboard.Properties.IsOffscreen.Value).IsFalse();

        // Cleanup — toggle keyboard off
        toolbar = _fixture.FindWindow("Toolbar_Window");
        kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        if (kbBtn != null)
            await _fixture.SimulateDwellOn(kbBtn);

        _fixture.MoveCursorAway();
        await Task.Delay(500, TestContext.Current.CancellationToken);
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
            Check.That(settingsBtn).IsNotNull();
            await _fixture.SimulateDwellOn(settingsBtn!);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            settings = _fixture.FindWindow("Settings_Window");
        }
        Check.That(settings).IsNotNull();

        var keyboardTab = _fixture.FindElement(settings!, "Settings_Tab_Keyboard");
        Check.That(keyboardTab).IsNotNull();
        await _fixture.SimulateDwellOn(keyboardTab!);
        await Task.Delay(500, TestContext.Current.CancellationToken); // wait for tab content to rebuild

        // Find the Click Sound switch.
        //
        // Settings_Toggle_{id} now names the STATE WORD inside DwellToggle's switch, not the row's
        // label. Two things changed under this test when DwellToggle became a composite. The label
        // is decorative now — dwelling it activates nothing — so the id moved onto the half that
        // really is a dwell target. And the checkbox glyph is gone: state is carried by the word
        // ("On"/"Off"), the knob's side and the track's fill, and the accessible name DwellToggle
        // composes is "{label}: {state}". So this asserts the state word rather than a glyph that
        // no longer exists.
        settings = _fixture.FindWindow("Settings_Window");
        var toggle = await _fixture.WaitForElementAsync(settings!, "Settings_Toggle_k_sound");
        var enabledText = toggle.Properties.Name.Value;
        Check.That(enabledText).Contains("Click Sound");
        Check.That(enabledText).EndsWith(": On"); // enabled by default

        // Act — dwell to disable. The state word sits inside the switch, well within the hit
        // circle the switch registers — pinned headlessly by
        // DwellToggleVisualTests.TheUiaAnchorsCentre_FallsInsideTheSwitchesHitCircle, because a
        // Border carries no AutomationPeer and so cannot hold the id itself.
        await _fixture.SimulateDwellOn(toggle);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Assert — toggle now shows disabled
        settings = _fixture.FindWindow("Settings_Window");
        toggle = _fixture.FindElement(settings!, "Settings_Toggle_k_sound")!;
        Check.That(toggle.Properties.Name.Value).EndsWith(": Off");

        // Cleanup — restore to enabled
        await _fixture.SimulateDwellOn(toggle);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        _fixture.MoveCursorAway();
    }

    [Fact]
    public async Task Keyboard_HasKeys()
    {
        // Arrange — ensure keyboard starts closed so we can open it fresh
        await _fixture.EnsureKeyboardClosedAsync();
        // Open keyboard
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Check.That(toolbar).IsNotNull();
        var kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        Check.That(kbBtn).IsNotNull();
        await _fixture.SimulateDwellOn(kbBtn!);

        // Wait for keyboard window to appear in the UIA tree.
        var keyboard = await _fixture.WaitForWindowAsync(
            "Keyboard_Window", TimeSpan.FromSeconds(5));

        var keys = _fixture.FindElements(keyboard!, "Keyboard_Key_");
        Check.That(keys.Length > 0).IsTrue();

        // Cleanup — toggle keyboard off
        toolbar = _fixture.FindWindow("Toolbar_Window");
        kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        if (kbBtn != null)
            await _fixture.SimulateDwellOn(kbBtn);

        _fixture.MoveCursorAway();
        await Task.Delay(500, TestContext.Current.CancellationToken);
    }
}
