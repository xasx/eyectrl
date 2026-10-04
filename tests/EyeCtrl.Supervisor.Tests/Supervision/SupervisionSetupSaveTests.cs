namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// The setup window's save-time merge for the one supervision key the App may also write.
/// A running supervisor holds a startup snapshot; without the merge, saving the window after the
/// App's Settings row turned supervision on would silently write the stale false back to disk.
/// </summary>
public class SupervisionSetupSaveTests
{
    [Fact]
    public void UntouchedToggle_AdoptsTheOnDiskValue()
    {
        // The App turned supervision on while the window sat open; the user changed nothing.
        Check.That(SupervisionSetupSave.ResolveEnabled(current: false, atOpen: false, onDisk: true))
            .IsTrue();
    }

    [Fact]
    public void UntouchedToggle_AdoptsAnOnDiskOff()
    {
        // The mirror: the App turned it off while the window sat open with it on.
        Check.That(SupervisionSetupSave.ResolveEnabled(current: true, atOpen: true, onDisk: false))
            .IsFalse();
    }

    [Fact]
    public void ToggledOn_BeatsADiskThatSaysOff()
    {
        Check.That(SupervisionSetupSave.ResolveEnabled(current: true, atOpen: false, onDisk: false))
            .IsTrue();
    }

    [Fact]
    public void ToggledOff_BeatsADiskThatSaysOn()
    {
        Check.That(SupervisionSetupSave.ResolveEnabled(current: false, atOpen: true, onDisk: true))
            .IsFalse();
    }

    [Fact]
    public void UntouchedAndUnchanged_KeepsTheValue()
    {
        Check.That(SupervisionSetupSave.ResolveEnabled(current: true, atOpen: true, onDisk: true))
            .IsTrue();
        Check.That(SupervisionSetupSave.ResolveEnabled(current: false, atOpen: false, onDisk: false))
            .IsFalse();
    }
}
