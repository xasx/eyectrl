namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

/// <summary>
/// B6's two NEW knobs, <see cref="DwellSettings.RestartNoticeDwellMs"/> and
/// <see cref="DwellSettings.EyeCareBannerDwellMs"/>: both default 0 = follow
/// <see cref="DwellSettings.ActivationButtonDwellMs"/> live, and a value above zero is 200–3000.
/// A new property means an existing settings file picks the default up from the initializer; no
/// existing default changes.
/// </summary>
public class RestartAndBannerDwellSettingTests
{
    [Fact]
    public void BothKnobs_DefaultToZero_AndFollowTheActivationDwell()
    {
        var dwell = new DwellSettings();

        Check.That(dwell.RestartNoticeDwellMs).IsEqualTo(0);
        Check.That(dwell.EyeCareBannerDwellMs).IsEqualTo(0);
        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(dwell.ActivationButtonDwellMs);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(dwell.ActivationButtonDwellMs);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Fact]
    public void AZeroKnob_FollowsTheActivationDwellLive()
    {
        var dwell = new DwellSettings();
        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(700);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(700);

        dwell.ActivationButtonDwellMs = 900;

        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(900);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(900);
    }

    [Fact]
    public void APositiveKnob_WinsOverTheActivationDwell_AndDoesNotFollowIt()
    {
        var dwell = new DwellSettings
        {
            RestartNoticeDwellMs = 1200,
            EyeCareBannerDwellMs = 1400,
            ActivationButtonDwellMs = 900,
        };

        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(1200);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(1400);

        dwell.ActivationButtonDwellMs = 500;

        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(1200);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(1400);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(3000)]
    public void BothKnobs_AcceptZeroAndTheRangeEnds(int value)
    {
        var dwell = new DwellSettings
        {
            RestartNoticeDwellMs = value,
            EyeCareBannerDwellMs = value,
        };

        Check.That(dwell.RestartNoticeDwellMs).IsEqualTo(value);
        Check.That(dwell.EyeCareBannerDwellMs).IsEqualTo(value);
        Check.That(dwell.Validate()).IsEmpty();
    }

    /// <summary>
    /// The stepper's real path back to "follow the activation dwell": from a positive value down
    /// to 0. The object-initializer case above cannot prove the guard accepts 0 — the generated
    /// setter short-circuits when the new value equals the field (and the default IS 0) — so a
    /// guard that rejects 0 would still pass it. This is the mutation-proof half.
    /// </summary>
    [Fact]
    public void BothKnobs_ReturnToZero_FromAPositiveValue()
    {
        var dwell = new DwellSettings
        {
            RestartNoticeDwellMs = 500,
            EyeCareBannerDwellMs = 600,
        };

        dwell.RestartNoticeDwellMs = 0;
        dwell.EyeCareBannerDwellMs = 0;

        Check.That(dwell.RestartNoticeDwellMs).IsEqualTo(0);
        Check.That(dwell.EyeCareBannerDwellMs).IsEqualTo(0);
        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(dwell.ActivationButtonDwellMs);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(dwell.ActivationButtonDwellMs);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(199)]
    [InlineData(3001)]
    public void BothKnobs_RejectValuesOutsideZeroAndTheRange(int value)
    {
        var dwell = new DwellSettings();

        Check.ThatCode(() => dwell.RestartNoticeDwellMs = value).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => dwell.EyeCareBannerDwellMs = value).Throws<ArgumentOutOfRangeException>();
        Check.That(dwell.RestartNoticeDwellMs).IsEqualTo(0);
        Check.That(dwell.EyeCareBannerDwellMs).IsEqualTo(0);
    }

    [Fact]
    public void Validate_FlagsAnOutOfRangeKnobLoadedAroundTheGuard()
    {
        var dwell = new DwellSettings();
        typeof(DwellSettings).GetField("_restartNoticeDwellMs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(dwell, 100);
        typeof(DwellSettings).GetField("_eyeCareBannerDwellMs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(dwell, 3001);

        var errors = dwell.Validate();

        Check.That(errors).HasElementThatMatches(e => e.Contains(nameof(DwellSettings.RestartNoticeDwellMs)));
        Check.That(errors).HasElementThatMatches(e => e.Contains(nameof(DwellSettings.EyeCareBannerDwellMs)));
    }

    [Fact]
    public void AnOldSettingsFileWithoutTheKeys_LoadsTheDefaults_AndKeepsEveryOtherValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_b6knobs_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """
            {
              "dwell": { "activationButtonDwellMs": 900, "resumeDwellMs": 1200 }
            }
            """);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Dwell.RestartNoticeDwellMs).IsEqualTo(0);
            Check.That(loaded.Dwell.EyeCareBannerDwellMs).IsEqualTo(0);
            Check.That(loaded.Dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(900);
            Check.That(loaded.Dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(900);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(900);
            Check.That(loaded.Dwell.ResumeDwellMs).IsEqualTo(1200);
        }
        finally { File.Delete(path); }
    }
}
