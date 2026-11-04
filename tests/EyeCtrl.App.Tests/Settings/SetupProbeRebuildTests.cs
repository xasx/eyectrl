namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Review 02 R2/R3: the two conditions on the catalogue probe's completion rebuild. The window's
/// own entry is a source pin in <see cref="EyeCtrl.App.Tests.Services.SettingsSetupWiringTests" />;
/// these are the decisions themselves.
/// </summary>
public class SetupProbeRebuildTests
{
    [Fact]
    public void TheAdvancedTab_IsTheOnlyTabThatRebuilds()
    {
        // R2: an update row exists only on „Erweitert"; every other tab has nothing to change,
        // and rebuilding it would throw away the owner's in-progress dwell.
        Check.That(SetupProbeRebuild.IsAdvancedTab("Advanced")).IsTrue();

        Check.That(SetupProbeRebuild.IsAdvancedTab("Cursor")).IsFalse();
        Check.That(SetupProbeRebuild.IsAdvancedTab("General")).IsFalse();
        Check.That(SetupProbeRebuild.IsAdvancedTab("advanced")).IsFalse();
        Check.That(SetupProbeRebuild.IsAdvancedTab("")).IsFalse();
        Check.That(SetupProbeRebuild.IsAdvancedTab(null)).IsFalse();
    }

    [Fact]
    public void AnOpenWindowOnTheAdvancedTab_Rebuilds()
    {
        Check.That(SetupProbeRebuild.ShouldRebuild(windowClosed: false, currentTabName: "Advanced")).IsTrue();
    }

    [Fact]
    public void AnOpenWindowOnAnotherTab_DoesNotRebuild()
    {
        Check.That(SetupProbeRebuild.ShouldRebuild(windowClosed: false, currentTabName: "Keyboard")).IsFalse();
    }

    [Fact]
    public void AWindowClosedBeforeTheDispatch_DoesNotRebuild()
    {
        // R3: the check is evaluated INSIDE the dispatched action, so a close in the gap between
        // the worker's completion and the queue drops the rebuild instead of touching a disposed
        // view model.
        Check.That(SetupProbeRebuild.ShouldRebuild(windowClosed: true, currentTabName: "Advanced")).IsFalse();
        Check.That(SetupProbeRebuild.ShouldRebuild(windowClosed: true, currentTabName: "Cursor")).IsFalse();
    }
}
