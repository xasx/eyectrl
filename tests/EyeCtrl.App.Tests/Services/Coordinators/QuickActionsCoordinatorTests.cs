namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Settings;
using Xunit;

[Collection("DpiHelper Tests (App)")]
public class QuickActionsCoordinatorTests
{
    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var c = new QuickActionsCoordinator();
        Check.ThatCode(() => c.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Mirrors the EventCoordinator contract test: a constructed-but-uninitialized
        // coordinator must still dispose cleanly (no NullRef on _context).
        new QuickActionsCoordinator().Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = new QuickActionsCoordinator();
        c.Dispose(); c.Dispose();
    }

    [Fact]
    public void Enabled_Toggle_SuspendsAndResumesSummon()
    {
        var settings = new EyeCtrlSettings { QuickActions = { Enabled = false } };
        var controller = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        var coord = new QuickActionsCoordinator();
        coord.InjectSummonForTesting(controller);           // bypass the GridVM/GazeEngine creation path
        coord.Initialize(TestFakes.MakeContext(settings));  // SetupQuickActionsSummon sees _summon != null
                                                            // → SetSuspended(!Enabled = true)
        Check.That(controller.IsSuspended).IsTrue();

        settings.QuickActions.Enabled = true;               // [ObservableProperty] raises PropertyChanged
                                                            // synchronously → the settings handler → SetSuspended(false)
        Check.That(controller.IsSuspended).IsFalse();
    }

    [Fact]
    public void SetSummonSuspended_ForwardsToTheController()
    {
        // The seam Keyboard/Settings call when a modal opens — the quick-actions half of the fan-out
        // asserted end-to-end in KeyboardCoordinatorTests.ApplyModalKeyboardState_FansOutTo...
        var controller = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        var coord = new QuickActionsCoordinator();
        coord.InjectSummonForTesting(controller);

        coord.SetSummonSuspended(true);
        Check.That(coord.IsSummonSuspended).IsTrue();

        coord.SetSummonSuspended(false);
        Check.That(coord.IsSummonSuspended).IsFalse();
    }

    [Fact]
    public void IsSummonSuspended_ReadsTrue_WhenNoControllerExists()
    {
        // Fail-closed default: with no controller (mouse mode, or QuickActions disabled) the summon is
        // effectively suspended, so the seam must not report "live".
        Check.That(new QuickActionsCoordinator().IsSummonSuspended).IsTrue();
    }
}
