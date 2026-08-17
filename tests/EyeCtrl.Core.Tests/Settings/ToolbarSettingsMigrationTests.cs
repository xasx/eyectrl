namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class ToolbarSettingsMigrationTests
{
    // 2026-08-17 mis-dwell fix: the old 80-DIP default with 2-px margins left adjacent hit
    // circles near-tangent and the user mis-dwelled constantly. The new default is 120 — but a
    // PERSISTED 80 (the old default, saved in the user's settings.json) would keep overriding
    // it, so startup runs a once-only bump. These pin the bump's edges.
    [Fact]
    public void MigrateSizeOnce_BumpsTheOldDefault_ExactlyOnce()
    {
        var settings = new ToolbarSettings { Size = 80 };

        settings.MigrateSizeOnce();

        Check.That(settings.Size).IsEqualTo(120);
        Check.That(settings.SizeMigrated).IsTrue();

        // The user slid back down after migrating: their choice sticks, no re-bump.
        settings.Size = 90;
        settings.MigrateSizeOnce();
        Check.That(settings.Size).IsEqualTo(90);
    }

    [Fact]
    public void MigrateSizeOnce_LeavesDeliberateSizesAlone()
    {
        var settings = new ToolbarSettings { Size = 150 };

        settings.MigrateSizeOnce();

        Check.That(settings.Size).IsEqualTo(150);
        Check.That(settings.SizeMigrated).IsTrue();
    }

    [Fact]
    public void FreshSettings_DefaultToTheEnlargedSize()
    {
        var settings = new ToolbarSettings();

        Check.That(settings.Size).IsEqualTo(120);
    }
}
