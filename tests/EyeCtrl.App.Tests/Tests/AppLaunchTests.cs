namespace EyeCtrl.App.Tests.Tests;

using System;
using System.Linq;
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
    public void Toolbar_RendersExactlyTheExpectedButtons()
    {
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Check.That(toolbar).IsNotNull();

        // Asserting the SET, not a count. This replaces a count test plus a non-exhaustive id list —
        // a pair that rotted twice, because these tests are Category=UI and skipped by default, so
        // nothing ran to notice. A set assertion cannot rot that way: it names whatever was added or
        // removed. ToolbarButtonDriftTests additionally guards this headlessly.
        // Must match the ids rendered by ToolbarWindow.Initialize (AutomationId = Toolbar_Button_{id}).
        // Order matches the View's buttons array. The optional provider indicator carries no
        // "Toolbar_Button_" id, so it is not included. The window-action trio moved to the Quick
        // Actions toolbar (2026-08-09 migration).
        var expectedIds = new[]
        {
            "Toolbar_Button_Pause",
            "Toolbar_Button_SelectionMode",
            "Toolbar_Button_Keyboard",
            "Toolbar_Button_Settings",
            "Toolbar_Button_TaskSwitcher",
            "Toolbar_Button_ResetAdaptation"
        };

        var actualIds = _fixture.FindElements(toolbar!, "Toolbar_Button_")
            .Select(e => e.Properties.AutomationId.Value)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Check.WithCustomMessage(
                "Rendered toolbar buttons drifted from the expected set. Rendered: "
                + string.Join(", ", actualIds))
            .That(actualIds)
            .ContainsExactly(expectedIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
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
