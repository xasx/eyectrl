namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// B2a's <see cref="DwellSettings.ResumeDwellMs"/>: a NEW property (default 1000 ms) with its own
/// validation, reaching an existing settings.json through its initializer. The Settings row comes in
/// B6; this ticket owns only the property and its range.
/// </summary>
public class ResumeDwellSettingTests
{
    [Fact]
    public void ResumeDwellMs_DefaultsTo1000_AndValidates()
    {
        var dwell = new DwellSettings();

        Check.That(dwell.ResumeDwellMs).IsEqualTo(1000);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(500)]
    [InlineData(3000)]
    public void ResumeDwellMs_AcceptsTheRangeEnds(int value)
    {
        var dwell = new DwellSettings { ResumeDwellMs = value };

        Check.That(dwell.ResumeDwellMs).IsEqualTo(value);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(499)]
    [InlineData(3001)]
    public void ResumeDwellMs_RejectsOutOfRange(int value)
    {
        var dwell = new DwellSettings();

        Check.ThatCode(() => dwell.ResumeDwellMs = value).Throws<ArgumentOutOfRangeException>();
        Check.That(dwell.ResumeDwellMs).IsEqualTo(1000);
    }

    [Fact]
    public void Validate_FlagsAnOutOfRangeResumeDwellLoadedAroundTheGuard()
    {
        var dwell = new DwellSettings();
        typeof(DwellSettings).GetField("_resumeDwellMs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(dwell, 100);

        Check.That(dwell.Validate()).HasElementThatMatches(e => e.Contains(nameof(DwellSettings.ResumeDwellMs)));
    }

    [Fact]
    public void AnOldSettingsFileWithoutTheKey_LoadsTheDefault_AndKeepsEveryOtherValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_resumedwell_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """
            {
              "dwell": { "toolbarButtonDwellMs": 900, "dwellPauseGraceMs": 300 }
            }
            """);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Dwell.ResumeDwellMs).IsEqualTo(1000);
            Check.That(loaded.Dwell.ToolbarButtonDwellMs).IsEqualTo(900);
            Check.That(loaded.Dwell.DwellPauseGraceMs).IsEqualTo(300);
        }
        finally { File.Delete(path); }
    }
}
