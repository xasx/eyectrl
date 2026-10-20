namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// The magnifier's stronger defaults (decision 1(a)/3(a)): every NEW install starts at 4x with a
/// 100-DIP circle. Fresh installs only - decision 2(a) leaves every existing settings file alone,
/// so there is no migration and no first-launch seed, and the owner's own file (4 / 100) is
/// untouched by construction. All file work happens in a TEMP file; the real settings file is
/// never read or written.
/// </summary>
public class ManualSelectionDefaultsTests
{
    [Fact]
    public void Defaults_AreFourTimes_WithTheOwnersCircle()
    {
        var settings = new ManualSelectionSettings();

        Check.WithCustomMessage("a new install starts at 4x")
            .That(settings.MagnificationLevel).IsEqualTo(4.0);
        Check.WithCustomMessage("a new install starts with the owner's 100-DIP circle")
            .That(settings.CircleRadiusPixels).IsEqualTo(100);
    }

    [Fact]
    public void OwnersFile_SurvivesLoadSaveReload_Unchanged()
    {
        // G2: the owner's real manualSelection section (4 / 100 plus their other values) goes
        // through Load -> Save -> reload with every value intact. A migration (or any load-time
        // overwrite) that moves these values makes this RED.
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var owner = new EyeCtrlSettings();
            owner.ManualSelection.CircleRadiusPixels = 100;
            owner.ManualSelection.MagnificationLevel = 4.0;
            owner.ManualSelection.ManualSelectionGracePeriodMs = 1200;
            owner.ManualSelection.HoldRadiusLogical = 200;
            owner.ManualSelection.StabilitySampleCount = 3;
            owner.ManualSelection.StabilityWindowSize = 7;
            owner.ManualSelection.HoldOutlierSkipLimit = 10;
            owner.ManualSelection.GazeIndicatorOpacity = 0.7;

            var service = new SettingsService(path);
            service.Save(owner);

            var loaded = service.Load();
            AssertOwnersSection(loaded);

            // The second leg: Save what Load produced, reload again - still the owner's values.
            service.Save(loaded);
            AssertOwnersSection(new SettingsService(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OldDefaultsFile_StaysAtTwoAndOneFifty()
    {
        // G2b: decision 2(a) - existing installs are left alone. A file holding the OLD defaults
        // (2.0 / 150) loads and saves back as 2.0 / 150; any migration that moves them is RED.
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var json = """
            {
                "manualSelection": {
                    "circleRadiusPixels": 150,
                    "magnificationLevel": 2.0
                }
            }
            """;
            File.WriteAllText(path, json);

            var service = new SettingsService(path);
            var loaded = service.Load();
            Check.That(loaded.ManualSelection.CircleRadiusPixels).IsEqualTo(150);
            Check.That(loaded.ManualSelection.MagnificationLevel).IsEqualTo(2.0);

            service.Save(loaded);
            var again = new SettingsService(path).Load();
            Check.WithCustomMessage("the old defaults must survive the save round-trip untouched")
                .That(again.ManualSelection.CircleRadiusPixels).IsEqualTo(150);
            Check.That(again.ManualSelection.MagnificationLevel).IsEqualTo(2.0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFile_ReturnsTheNewDefaults()
    {
        // G3: a missing settings file is the fresh install - the new defaults, never a seed.
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_test_missing_{Guid.NewGuid()}.json");

        var settings = new SettingsService(path).Load();

        Check.That(settings.ManualSelection.CircleRadiusPixels).IsEqualTo(100);
        Check.That(settings.ManualSelection.MagnificationLevel).IsEqualTo(4.0);
    }

    private static void AssertOwnersSection(EyeCtrlSettings settings)
    {
        Check.That(settings.ManualSelection.CircleRadiusPixels).IsEqualTo(100);
        Check.That(settings.ManualSelection.MagnificationLevel).IsEqualTo(4.0);
        Check.That(settings.ManualSelection.ManualSelectionGracePeriodMs).IsEqualTo(1200);
        Check.That(settings.ManualSelection.HoldRadiusLogical).IsEqualTo(200.0);
        Check.That(settings.ManualSelection.StabilitySampleCount).IsEqualTo(3);
        Check.That(settings.ManualSelection.StabilityWindowSize).IsEqualTo(7);
        Check.That(settings.ManualSelection.HoldOutlierSkipLimit).IsEqualTo(10);
        Check.That(settings.ManualSelection.GazeIndicatorOpacity).IsEqualTo(0.7);
    }
}
