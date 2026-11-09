namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// Round button B3's <see cref="DwellSettings.OfferShowAfterMs"/>: a NEW property (default 1000 ms,
/// range 500–2500) reaching an existing settings.json through its initializer, so an old file never
/// fails validation over it.
/// </summary>
public class OfferShowAfterSettingTests
{
    [Fact]
    public void OfferShowAfterMs_DefaultsTo1000_AndValidates()
    {
        var dwell = new DwellSettings();

        Check.That(dwell.OfferShowAfterMs).IsEqualTo(1000);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2500)]
    public void OfferShowAfterMs_AcceptsTheRangeEnds(int value)
    {
        var dwell = new DwellSettings { OfferShowAfterMs = value };

        Check.That(dwell.OfferShowAfterMs).IsEqualTo(value);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(499)]
    [InlineData(2501)]
    public void OfferShowAfterMs_RejectsOutOfRange(int value)
    {
        var dwell = new DwellSettings();

        Check.ThatCode(() => dwell.OfferShowAfterMs = value).Throws<ArgumentOutOfRangeException>();
        Check.That(dwell.OfferShowAfterMs).IsEqualTo(1000);
    }

    [Fact]
    public void Validate_FlagsAnOutOfRangeValueLoadedAroundTheGuard()
    {
        var dwell = new DwellSettings();
        typeof(DwellSettings).GetField("_offerShowAfterMs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(dwell, 100);

        Check.That(dwell.Validate()).HasElementThatMatches(e => e.Contains(nameof(DwellSettings.OfferShowAfterMs)));
    }

    [Fact]
    public void AnOldSettingsFileWithoutTheKey_Loads1000_AndKeepsEveryOtherValue()
    {
        // Mutation: a validation that rejects the missing key (or a default outside the range) →
        // Load falls back to defaults and the 900 / 1200 / 300 are lost, red.
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_offershowafter_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """
            {
              "dwell": { "activationButtonDwellMs": 900, "resumeDwellMs": 1200, "dwellPauseGraceMs": 300 }
            }
            """);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Dwell.OfferShowAfterMs).IsEqualTo(1000);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(900);
            Check.That(loaded.Dwell.ResumeDwellMs).IsEqualTo(1200);
            Check.That(loaded.Dwell.DwellPauseGraceMs).IsEqualTo(300);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheValue_RoundTripsThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_offershowafter_rt_{Guid.NewGuid()}.json");
        try
        {
            var service = new SettingsService(path);
            var settings = new EyeCtrlSettings();
            settings.Dwell.OfferShowAfterMs = 1700;
            service.Save(settings);

            Check.That(service.Load().Dwell.OfferShowAfterMs).IsEqualTo(1700);
        }
        finally { File.Delete(path); }
    }
}
