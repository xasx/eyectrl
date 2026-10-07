namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>The <see cref="GeneralSettings.UiLanguage"/> setting: default <c>System</c> and a
/// defined-enum validation entry, so a hand-edited file cannot smuggle an undefined value in.</summary>
public class UiLanguageSettingTests
{
    [Fact]
    public void Default_IsSystem() =>
        Check.That(new GeneralSettings().UiLanguage).IsEqualTo(UiLanguage.System);

    [Theory]
    [InlineData(UiLanguage.System)]
    [InlineData(UiLanguage.German)]
    [InlineData(UiLanguage.English)]
    public void DefinedValues_ValidateClean(UiLanguage language)
    {
        var settings = new GeneralSettings { UiLanguage = language };
        Check.That(settings.Validate()).IsEmpty();
    }

    [Fact]
    public void UndefinedValue_FailsValidation()
    {
        var settings = new GeneralSettings { UiLanguage = (UiLanguage)99 };
        var errors = settings.Validate();

        Check.That(errors).HasSize(1);
        Check.That(errors[0]).Contains("UiLanguage");
    }
}
