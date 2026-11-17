namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// S4-A's <see cref="DwellSettings.TaskSwitcherTileDwellMs"/>: a NEW property (default 800 ms,
/// range 500–2500) reaching an existing settings.json through its initializer, so an old file
/// never fails validation over it. Same shape as <see cref="OfferShowAfterSettingTests"/> —
/// the effective dwell the switcher uses is max(tile, toolbar) and lives in the
/// TaskSwitcherViewModel; these pins hold the knob itself.
/// </summary>
public class TaskSwitcherTileDwellSettingTests
{
    [Fact]
    public void TaskSwitcherTileDwellMs_DefaultsTo800_AndValidates()
    {
        var dwell = new DwellSettings();

        Check.That(dwell.TaskSwitcherTileDwellMs).IsEqualTo(800);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2500)]
    public void TaskSwitcherTileDwellMs_AcceptsTheRangeEnds(int value)
    {
        var dwell = new DwellSettings { TaskSwitcherTileDwellMs = value };

        Check.That(dwell.TaskSwitcherTileDwellMs).IsEqualTo(value);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(499)]
    [InlineData(2501)]
    public void TaskSwitcherTileDwellMs_RejectsOutOfRange(int value)
    {
        var dwell = new DwellSettings();

        Check.ThatCode(() => dwell.TaskSwitcherTileDwellMs = value).Throws<ArgumentOutOfRangeException>();
        Check.That(dwell.TaskSwitcherTileDwellMs).IsEqualTo(800);
    }

    [Fact]
    public void Validate_FlagsAnOutOfRangeValueLoadedAroundTheGuard()
    {
        var dwell = new DwellSettings();
        typeof(DwellSettings).GetField("_taskSwitcherTileDwellMs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(dwell, 300);

        Check.That(dwell.Validate()).HasElementThatMatches(e => e.Contains(nameof(DwellSettings.TaskSwitcherTileDwellMs)));
    }

    [Fact]
    public void AnOldSettingsFileWithoutTheKey_Loads800_AndKeepsEveryOtherValue()
    {
        // Mutation: a validation that rejects the missing key (or a default outside the range) →
        // Load falls back to defaults and the 600 / 900 / 450 are lost, red.
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_tstile_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """
            {
              "dwell": { "toolbarButtonDwellMs": 600, "resumeDwellMs": 900, "keyboardKeyDwellMs": 450 }
            }
            """);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Dwell.TaskSwitcherTileDwellMs).IsEqualTo(800);
            Check.That(loaded.Dwell.ToolbarButtonDwellMs).IsEqualTo(600);
            Check.That(loaded.Dwell.ResumeDwellMs).IsEqualTo(900);
            Check.That(loaded.Dwell.KeyboardKeyDwellMs).IsEqualTo(450);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheValue_RoundTripsThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_tstile_rt_{Guid.NewGuid()}.json");
        try
        {
            var service = new SettingsService(path);
            var settings = new EyeCtrlSettings();
            settings.Dwell.TaskSwitcherTileDwellMs = 1200;
            service.Save(settings);

            Check.That(service.Load().Dwell.TaskSwitcherTileDwellMs).IsEqualTo(1200);
        }
        finally { File.Delete(path); }
    }
}
