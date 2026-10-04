namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.IO;
using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// The setup window's save-time merge for the one supervision key the App may also write.
/// A running supervisor holds a startup snapshot; without the merge, saving the window after the
/// App's Settings row turned supervision on would silently write the stale false back to disk.
/// </summary>
public class SupervisionSetupSaveTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"eyectrl_sup_setup_{Guid.NewGuid()}.json");
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

    // ── The window's state object: open on disk, re-read on save ─────────────────────────────

    [Fact]
    public void Constructor_ReadsTheOnDiskValue_NotTheStaleSnapshot()
    {
        // F7 pin: the window opens on what disk says NOW — the App's row may have turned
        // supervision on after this supervisor process loaded its startup snapshot.
        var path = TempPath();
        try
        {
            var service = new SettingsService(path, SettingsWriter.Supervisor);
            service.Save(new EyeCtrlSettings());                 // supervisor's startup snapshot: off

            var written = service.Load();
            written.Supervision.Enabled = true;                  // the App's row wrote true since
            service.Save(written);

            var state = new SupervisionSetupEnabledState(service);

            Check.That(state.EnabledAtOpen).IsTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveForSave_Untouched_ReReadsTheOnDiskValue()
    {
        // S1 pin: the save must re-read disk, not reuse the value captured when the window
        // opened; the App can flip the flag while the window sits open.
        var path = TempPath();
        try
        {
            var service = new SettingsService(path, SettingsWriter.Supervisor);
            var seed = new EyeCtrlSettings();
            seed.Supervision.Enabled = true;
            service.Save(seed);

            var state = new SupervisionSetupEnabledState(service);   // opens on true

            var flipped = service.Load();
            flipped.Supervision.Enabled = false;                     // the App turned it off since
            service.Save(flipped);

            Check.That(state.ResolveForSave(current: state.EnabledAtOpen)).IsFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveForSave_UserChangedTheToggle_WinsOverDisk()
    {
        var path = TempPath();
        try
        {
            var service = new SettingsService(path, SettingsWriter.Supervisor);
            service.Save(new EyeCtrlSettings());                     // opens on off

            var state = new SupervisionSetupEnabledState(service);

            Check.That(state.ResolveForSave(current: true)).IsTrue();
        }
        finally { File.Delete(path); }
    }
}
