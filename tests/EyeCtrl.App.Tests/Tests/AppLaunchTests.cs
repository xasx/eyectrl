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
        AppFixture.SkipIfDisabled();
        _fixture = fixture;
    }

    [Fact]
    public void App_LaunchesInTestMode_CursorCircleIsVisible()
    {
        var window = _fixture.FindWindow("CursorCircle_Window");

        Check.That(window).IsNotNull();
        Check.That(window!.Properties.IsOffscreen.Value).IsFalse();
    }

    [Fact]
    public void App_LaunchesInTestMode_ToolbarIsVisible()
    {
        var window = _fixture.FindWindow("Toolbar_Window");

        Check.That(window).IsNotNull();
        Check.That(window!.Properties.IsOffscreen.Value).IsFalse();
    }

    [Fact]
    public void Toolbar_HasEightButtons()
    {
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Check.That(toolbar).IsNotNull();

        var buttons = _fixture.FindElements(toolbar!, "Toolbar_Button_");

        // ToolbarWindow.Initialize renders exactly eight buttons (Pause, SelectionMode, Keyboard,
        // Settings, ResetAdaptation, plus Minimize/MaximizeRestore/Close when ShowWindowActions=true,
        // which is the default); the optional provider indicator carries no "Toolbar_Button_"
        // AutomationId, so it is not counted here.
        Check.That(buttons.Length).IsEqualTo(8);
    }

    [Fact]
    public void Toolbar_ButtonsHaveCorrectIds()
    {
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Check.That(toolbar).IsNotNull();

        // Must match the ids rendered by ToolbarWindow.Initialize (AutomationId = Toolbar_Button_{id}).
        // Order matches the View's buttons array; Minimize/MaximizeRestore/Close are gated by
        // ShowWindowActions=true (the default).
        var expectedIds = new[]
        {
            "Toolbar_Button_Pause",
            "Toolbar_Button_SelectionMode",
            "Toolbar_Button_Keyboard",
            "Toolbar_Button_Settings",
            "Toolbar_Button_ResetAdaptation",
            "Toolbar_Button_Minimize",
            "Toolbar_Button_MaximizeRestore",
            "Toolbar_Button_Close"
        };

        foreach (var id in expectedIds)
        {
            var button = _fixture.FindElement(toolbar!, id);
            Check.That(button).IsNotNull();
        }
    }

    [Fact]
    public void CursorCircle_HasMainEllipse()
    {
        var cursorCircle = _fixture.FindWindow("CursorCircle_Window");
        Check.That(cursorCircle).IsNotNull();

        var ellipse = _fixture.FindElement(cursorCircle!, "CursorCircle_Ellipse_Main");
        Check.That(ellipse).IsNotNull();
    }
}
