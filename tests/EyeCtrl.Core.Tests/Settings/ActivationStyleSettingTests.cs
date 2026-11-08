namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// Rest activation R1: <see cref="DwellSettings.ActivationStyle"/> (default
/// <see cref="ActivationStyle.RestFill"/>) and <see cref="DwellSettings.RestFillMs"/> (default 700,
/// 200–2000). Both are NEW properties, so an existing settings.json reaches them through their
/// initializers — and must not trip a guard that would wipe the file.
/// </summary>
public class ActivationStyleSettingTests
{
    [Fact]
    public void TheDefaults_AreRestFillAnd700_AndValidate()
    {
        // R1-M10 (RestFillMs default 500) reds the second check.
        var dwell = new DwellSettings();

        Check.That(dwell.ActivationStyle).IsEqualTo(ActivationStyle.RestFill);
        Check.That(dwell.RestFillMs).IsEqualTo(700);
        Check.That(dwell.ActivationButtonDwellMs).IsEqualTo(700);   // the OfferButton dwell, untouched
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(200)]
    [InlineData(2000)]
    public void RestFillMs_AcceptsTheRangeEnds(int value)
    {
        var dwell = new DwellSettings { RestFillMs = value };

        Check.That(dwell.RestFillMs).IsEqualTo(value);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(199)]
    [InlineData(2001)]
    public void RestFillMs_RejectsOutOfRange(int value)
    {
        var dwell = new DwellSettings();

        Check.ThatCode(() => dwell.RestFillMs = value).Throws<ArgumentOutOfRangeException>();
        Check.That(dwell.RestFillMs).IsEqualTo(700);
    }

    [Fact]
    public void ActivationStyle_RejectsAnUndefinedValue()
    {
        var dwell = new DwellSettings();

        Check.ThatCode(() => dwell.ActivationStyle = (ActivationStyle)99).Throws<ArgumentOutOfRangeException>();
        Check.That(dwell.ActivationStyle).IsEqualTo(ActivationStyle.RestFill);
    }

    [Fact]
    public void Validate_FlagsValuesLoadedAroundTheGuards()
    {
        var dwell = new DwellSettings();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(DwellSettings).GetField("_restFillMs", flags)!.SetValue(dwell, 100);
        typeof(DwellSettings).GetField("_activationStyle", flags)!.SetValue(dwell, (ActivationStyle)7);

        Check.That(dwell.Validate()).HasElementThatMatches(e => e.Contains(nameof(DwellSettings.RestFillMs)));
        Check.That(dwell.Validate()).HasElementThatMatches(e => e.Contains(nameof(DwellSettings.ActivationStyle)));
    }

    [Fact]
    public void AnOldSettingsFileWithoutTheKeys_LoadsRestFillAnd700_AndKeepsEveryOtherValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_activationstyle_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """
            {
              "selectionMode": "manualSelection",
              "dwell": { "activationButtonDwellMs": 500, "fixationRadiusPixels": 80, "toolbarButtonDwellMs": 900 }
            }
            """);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Dwell.ActivationStyle).IsEqualTo(ActivationStyle.RestFill);
            Check.That(loaded.Dwell.RestFillMs).IsEqualTo(700);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(500);
            Check.That(loaded.Dwell.FixationRadiusPixels).IsEqualTo(80.0);
            Check.That(loaded.Dwell.ToolbarButtonDwellMs).IsEqualTo(900);
            Check.That(loaded.SelectionMode).IsEqualTo(EyeCtrl.Core.Models.SelectionMode.ManualSelection);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheStyleAndFillTime_RoundTripThroughTheFile_AsCamelCaseStrings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_activationstyle_{Guid.NewGuid()}.json");
        try
        {
            var service = new SettingsService(path);
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationStyle = ActivationStyle.OfferButton;
            settings.Dwell.RestFillMs = 900;
            service.Save(settings);

            var json = File.ReadAllText(path);
            Check.That(System.Text.RegularExpressions.Regex.IsMatch(json, "\"activationStyle\"\\s*:\\s*\"offerButton\"")).IsTrue();
            var loaded = new SettingsService(path).Load();
            Check.That(loaded.Dwell.ActivationStyle).IsEqualTo(ActivationStyle.OfferButton);
            Check.That(loaded.Dwell.RestFillMs).IsEqualTo(900);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnUndefinedStyleInTheFile_DropsOnlyThatKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_activationstyle_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """
            { "dwell": { "activationStyle": 42, "restFillMs": 1100, "toolbarButtonDwellMs": 900 } }
            """);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Dwell.ActivationStyle).IsEqualTo(ActivationStyle.RestFill);
            Check.That(loaded.Dwell.RestFillMs).IsEqualTo(1100);
            Check.That(loaded.Dwell.ToolbarButtonDwellMs).IsEqualTo(900);
        }
        finally { File.Delete(path); }
    }
}
