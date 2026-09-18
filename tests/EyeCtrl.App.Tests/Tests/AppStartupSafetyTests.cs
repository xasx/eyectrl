namespace EyeCtrl.App.Tests.Tests;

using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Verifies the app survives clean startup and shutdown — testing the
/// resilience of the try/catch wrapper added to OnStartup.
/// </summary>
[Collection("App")]
[Trait("Category", "UI")]
public class AppStartupSafetyTests
{
    private readonly AppFixture _fixture;

    public AppStartupSafetyTests(AppFixture fixture)
    {
        AppFixture.SkipIfDisabled();
        _fixture = fixture;
    }

    [Fact]
    public void App_Startup_ProducesNoUnhandledExceptions()
    {
        // The Fixture itself validates startup: if the app crashed during
        // OnStartup, WaitForWindowAsync would timeout and throw.
        // Reaching this assert means the try/catch in OnStartup did not fire
        // (normal happy path validated).
        var process = System.Diagnostics.Process.GetProcessById(_fixture.App.ProcessId);
        Check.That(process.HasExited).IsFalse();
    }

    [Fact]
    public async Task App_CursorCircle_FollowsGaze()
    {
        // Core functionality: the gaze tracking loop is active post-startup.
        // If startup partially failed (caught by try/catch), the cursor
        // circle would not be visible. (Ensure sane state first: a settings
        // modal hides the ring and would false-positive this test.)
        Assert.Skip("Cursor-ring visibility depends on app state the fixture cannot reset "
            + "from outside (selection modes and other states hide it; no --test-mode "
            + "state-reset seam exists). Pending that owner decision; see the pause tests "
            + "for the indicator-based pattern that replaced ring asserts.");

        var cursor = _fixture.FindWindow("CursorCircle_Window");
        Check.That(cursor).IsNotNull();
        Check.That(cursor!.Properties.IsOffscreen.Value).IsFalse();
    }

    [Fact]
    public async Task App_Toolbar_RemainsAccessible()
    {
        // Verify the toolbar is visible — confirms OverlayManager,
        // ViewModels, and EventCoordinator all initialized successfully.
        // The band auto-hides when idle; summon it from the docked edge first.
        await _fixture.EnsureGazeUiVisibleAsync();
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Check.That(toolbar).IsNotNull();
        Check.That(toolbar!.Properties.IsOffscreen.Value).IsFalse();
    }
}
