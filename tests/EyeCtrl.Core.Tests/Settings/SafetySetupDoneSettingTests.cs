namespace EyeCtrl.Core.Tests.Settings;

using System.IO;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// <see cref="GeneralSettings.SafetySetupDone"/>: the one-time safety questions' "answered to the
/// end" flag. It must default to false — an existing <c>settings.json</c> without the key (the
/// owner's, written before this setting existed) asks once — and it must round-trip through the
/// file so an answered chain stays answered.
/// </summary>
public class SafetySetupDoneSettingTests
{
    [Fact]
    public void Default_IsFalse_SoAnExistingInstallAsksOnce() =>
        Check.That(new GeneralSettings().SafetySetupDone).IsFalse();

    [Fact]
    public void Validate_EitherValue_HasNoErrors()
    {
        Check.That(new GeneralSettings { SafetySetupDone = true }.Validate()).IsEmpty();
        Check.That(new GeneralSettings { SafetySetupDone = false }.Validate()).IsEmpty();
    }

    [Fact]
    public void ALegacyFileWithoutTheKey_LoadsFalse_AndKeepsItsOtherValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_safety_legacy_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "{ \"general\": { \"startOnBoot\": true } }");

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.General.SafetySetupDone).IsFalse();
            Check.That(loaded.General.StartOnBoot).IsTrue();
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void ATrueFlag_SurvivesSaveAndLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_safety_roundtrip_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            settings.General.SafetySetupDone = true;
            new SettingsService(path).Save(settings);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.General.SafetySetupDone).IsTrue();
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
