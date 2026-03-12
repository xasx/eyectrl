namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using System.Globalization;

public class LanguageDetectorTests
{
    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr-BE", "fr")]
    [InlineData("es-ES", "es")]
    [InlineData("es-MX", "es")]
    [InlineData("de-DE", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("pl-PL", "pl")]
    public void DetectFromCulture_KnownCulture_ReturnsMappedCode(string cultureName, string expected)
    {
        var culture = new CultureInfo(cultureName);
        Assert.Equal(expected, LanguageDetector.DetectFromCulture(culture));
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public void DetectFromCulture_UnsupportedCulture_FallsBackToEnglish(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        Assert.Equal("en", LanguageDetector.DetectFromCulture(culture));
    }
}
