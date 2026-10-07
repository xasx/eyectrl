namespace EyeCtrl.Core.Tests.Localization;

using System;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>The one mapping every language token goes through (critique-01 #20).</summary>
public class UiLanguageTokensTests
{
    [Theory]
    [InlineData(UiLanguage.System, "system")]
    [InlineData(UiLanguage.German, "german")]
    [InlineData(UiLanguage.English, "english")]
    public void JsonTokens_RoundTrip(UiLanguage language, string token)
    {
        Check.That(UiLanguageTokens.ToJson(language)).IsEqualTo(token);
        Check.That(UiLanguageTokens.TryFromJson(token, out var parsed)).IsTrue();
        Check.That(parsed).IsEqualTo(language);
    }

    [Theory]
    [InlineData(UiLanguage.System, "system")]
    [InlineData(UiLanguage.German, "de")]
    [InlineData(UiLanguage.English, "en")]
    public void CommandLineTokens_RoundTrip(UiLanguage language, string token)
    {
        Check.That(UiLanguageTokens.ToCommandLine(language)).IsEqualTo(token);
        Check.That(UiLanguageTokens.TryFromCommandLine(token, out var parsed)).IsTrue();
        Check.That(parsed).IsEqualTo(language);
    }

    [Theory]
    [InlineData("SYSTEM")]
    [InlineData("German")]
    [InlineData("DE")]
    [InlineData("En")]
    public void Tokens_AreCaseInsensitive(string token)
    {
        Check.That(
            UiLanguageTokens.TryFromJson(token, out _)
            || UiLanguageTokens.TryFromCommandLine(token, out _)).IsTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deutsch")]
    [InlineData("german-English")]
    [InlineData("3")]
    public void UnknownTokens_DoNotParse(string? token)
    {
        Check.That(UiLanguageTokens.TryFromJson(token, out _)).IsFalse();
        Check.That(UiLanguageTokens.TryFromCommandLine(token, out _)).IsFalse();
    }

    [Theory]
    [InlineData(UiLanguage.System, "System")]
    [InlineData(UiLanguage.German, "Deutsch")]
    [InlineData(UiLanguage.English, "English")]
    public void Endonyms_AreTheBilingualSafeNames(UiLanguage language, string endonym) =>
        Check.That(UiLanguageTokens.Endonym(language)).IsEqualTo(endonym);

    [Fact]
    public void UndefinedEnumValues_Throw()
    {
        foreach (var call in new Func<string>[]
        {
            () => UiLanguageTokens.ToJson((UiLanguage)99),
            () => UiLanguageTokens.ToCommandLine((UiLanguage)99),
            () => UiLanguageTokens.Endonym((UiLanguage)99)
        })
        {
            Check.ThatCode(call).Throws<ArgumentOutOfRangeException>();
        }
    }
}
