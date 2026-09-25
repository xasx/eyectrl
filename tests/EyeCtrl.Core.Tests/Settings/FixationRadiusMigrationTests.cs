namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// Ticket 3 row "Migration": the fixation radius default moves 50 → 75 and an existing file's persisted
/// value is replaced ONCE. Unlike <c>ToolbarSettings.MigrateSizeOnce</c>, any prior value is replaced on
/// the first run — the user's file holds 90, an unvalidated value from a paused session — and the
/// persisted flag is what lets a LATER user edit stick. The 10–200 guard is deliberately untouched so a
/// legal legacy value loads first instead of making Load reject (and wipe) the whole file.
/// <para>
/// Mutation: drop the persisted-flag guard → the second migration overwrites the user's later edit.
/// </para>
/// </summary>
public class FixationRadiusMigrationTests
{
    [Fact]
    public void FreshSettings_DefaultTo75()
    {
        Check.That(new DwellSettings().FixationRadiusPixels).IsEqualTo(75.0);
    }

    [Fact]
    public void MigrateFixationRadiusOnce_ReplacesAnyPriorValue_ExactlyOnce()
    {
        var settings = new DwellSettings { FixationRadiusPixels = 90 };
        Check.That(settings.FixationRadiusMigrated).IsFalse();

        settings.MigrateFixationRadiusOnce();

        Check.That(settings.FixationRadiusPixels).IsEqualTo(75.0);
        Check.That(settings.FixationRadiusMigrated).IsTrue();

        // The user then picks their own value; the next startup's migration must leave it alone.
        settings.FixationRadiusPixels = 60;
        settings.MigrateFixationRadiusOnce();
        Check.That(settings.FixationRadiusPixels).IsEqualTo(60.0);
    }

    [Fact]
    public void FileWith90_LoadsUnwiped_MigratesTo75_AndALaterEditPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_radiusmig_{Guid.NewGuid()}.json");
        try
        {
            // A pre-ticket-3 file: radius 90 plus an unrelated dwell value as the not-wiped witness.
            File.WriteAllText(path, """
            {
              "dwell": { "fixationRadiusPixels": 90, "activationButtonDwellMs": 850 }
            }
            """);
            var service = new SettingsService(path);

            var loaded = service.Load();
            Check.That(loaded.Dwell.FixationRadiusPixels).IsEqualTo(90.0);          // legal legacy value loads
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);        // file not wiped

            // The startup order App.xaml.cs uses: load, then migrate, then save.
            loaded.Dwell.MigrateFixationRadiusOnce();
            service.Save(loaded);

            var reloaded = service.Load();
            Check.That(reloaded.Dwell.FixationRadiusPixels).IsEqualTo(75.0);
            Check.That(reloaded.Dwell.FixationRadiusMigrated).IsTrue();
            Check.That(reloaded.Dwell.ActivationButtonDwellMs).IsEqualTo(850);      // still nothing else lost

            // The user's later edit survives the next startup's migration.
            reloaded.Dwell.FixationRadiusPixels = 60;
            service.Save(reloaded);
            var afterEdit = service.Load();
            afterEdit.Dwell.MigrateFixationRadiusOnce();
            Check.That(afterEdit.Dwell.FixationRadiusPixels).IsEqualTo(60.0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheGuardIsNotNarrowed_TheLegacy90DoesNotTripValidation()
    {
        // A narrowed OnFixationRadiusPixelsChanging guard is the file-wipe trap: SettingsService answers
        // a throw during deserialisation with all defaults, so the ticket's "do not narrow 10–200" is
        // pinned here as an oracle.
        var settings = new DwellSettings { FixationRadiusPixels = 90.0 };
        Check.That(settings.FixationRadiusPixels).IsEqualTo(90.0);
        Check.That(settings.Validate()).IsEmpty();
    }
}
