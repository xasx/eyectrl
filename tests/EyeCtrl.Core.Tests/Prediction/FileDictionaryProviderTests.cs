namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class FileDictionaryProviderTests
{
    [Fact]
    public void UserDictionaryPath_IncludesLanguageCode()
    {
        var provider = new FileDictionaryProvider(
            baseDictionaryPath: "base_en.json",
            appDataDir: @"C:\AppData",
            languageCode: "fr");

        Assert.Equal(@"C:\AppData\user_dictionary_fr.json", provider.UserDictionaryPath);
    }

    [Fact]
    public void UserDictionaryPath_DefaultsToEn()
    {
        var provider = new FileDictionaryProvider(
            baseDictionaryPath: "base.json",
            appDataDir: @"C:\AppData",
            languageCode: "en");

        Assert.Equal(@"C:\AppData\user_dictionary_en.json", provider.UserDictionaryPath);
    }

    [Fact]
    public void Load_MissingFiles_ReturnsEmptyDictionary()
    {
        var provider = new FileDictionaryProvider(
            baseDictionaryPath: "nonexistent_base.json",
            appDataDir: Path.GetTempPath(),
            languageCode: "en");

        var words = provider.Load();
        Assert.Empty(words);
    }
}
