namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// Verifies single-writer ownership enforcement in <see cref="SettingsService"/>.
/// The App process owns every section EXCEPT Supervision; the Supervisor process
/// owns ONLY Supervision. An App writer must never clobber a Supervision value the
/// supervisor persisted to disk (spec: "Lost updates, not just corruption").
/// </summary>
public class SettingsServiceWriterOwnershipTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");

    [Fact]
    public void AppWriter_DoesNotPersist_SupervisionSection()
    {
        var path = TempPath();
        try
        {
            // Supervisor wrote Enabled=true on disk.
            var supervisor = new SettingsService(path, SettingsWriter.Supervisor);
            var seed = new EyeCtrlSettings { Supervision = { Enabled = true } };
            supervisor.Save(seed);

            // App loads, then saves its own snapshot (Supervision.Enabled default false).
            var app = new SettingsService(path, SettingsWriter.App);
            var loaded = app.Load();
            Check.That(loaded.Supervision.Enabled).IsTrue(); // sees supervisor's value
            app.Save(loaded); // app must not clobber it

            var roundTripped = new SettingsService(path, SettingsWriter.App).Load();
            Check.That(roundTripped.Supervision.Enabled).IsTrue(); // preserved, not reset
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AppWriter_Preserves_OwnedSections()
    {
        // The App writer must still persist the sections it owns (everything but Supervision).
        var path = TempPath();
        try
        {
            var app = new SettingsService(path, SettingsWriter.App);
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 900;
            app.Save(settings);

            var loaded = new SettingsService(path, SettingsWriter.App).Load();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(900);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SupervisorWriter_DoesNotPersist_AppSections()
    {
        // Symmetric guarantee: the Supervisor writer must not clobber an App-owned section.
        var path = TempPath();
        try
        {
            // App wrote Dwell.ActivationButtonDwellMs=1234 on disk.
            var app = new SettingsService(path, SettingsWriter.App);
            var seed = new EyeCtrlSettings();
            seed.Dwell.ActivationButtonDwellMs = 1234;
            app.Save(seed);

            // Supervisor loads, flips its own section, and saves.
            var supervisor = new SettingsService(path, SettingsWriter.Supervisor);
            var loaded = supervisor.Load();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(1234); // sees app's value
            loaded.Supervision.Enabled = true;
            supervisor.Save(loaded);

            var roundTripped = new SettingsService(path, SettingsWriter.Supervisor).Load();
            Check.That(roundTripped.Supervision.Enabled).IsTrue();          // supervisor's write preserved
            Check.That(roundTripped.Dwell.ActivationButtonDwellMs).IsEqualTo(1234); // app's write preserved
        }
        finally { File.Delete(path); }
    }
}
