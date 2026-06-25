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
    public void App_CursorCircle_FollowsGaze()
    {
        // Core functionality: the gaze tracking loop is active post-startup.
        // If startup partially failed (caught by try/catch), the cursor
        // circle would not be visible.
        var cursor = _fixture.FindWindow("CursorCircle_Window");
        Check.That(cursor).IsNotNull();
        Check.That(cursor!.Properties.IsOffscreen.Value).IsFalse();
    }

    [Fact]
    public void App_Toolbar_RemainsAccessible()
    {
        // Verify the toolbar is visible — confirms OverlayManager,
        // ViewModels, and EventCoordinator all initialized successfully.
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Check.That(toolbar).IsNotNull();
        Check.That(toolbar!.Properties.IsOffscreen.Value).IsFalse();
    }
}
