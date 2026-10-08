namespace EyeCtrl.Core.Tests.Localization;

using System;
using System.Globalization;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// <see cref="StringTable.ResolveLanguage"/> is the rule T04's pending-restart note reads: whether
/// the SAVED selection resolves differently from the language this process started with. It must
/// match the rule the table itself applied at construction, which is why its cases mirror
/// <c>StringTableTests</c>' own resolution expectations.
/// </summary>
public class StringTableResolveLanguageTests
{
    [Theory]
    [InlineData("de-DE", UiLanguage.German)]
    [InlineData("de", UiLanguage.German)]
    [InlineData("en-US", UiLanguage.English)]
    [InlineData("fr-FR", UiLanguage.English)]
    public void System_FollowsTheCulture_ExactlyLikeTheTable(string culture, UiLanguage expected)
    {
        Check.That(StringTable.ResolveLanguage(UiLanguage.System, CultureInfo.GetCultureInfo(culture)))
            .IsEqualTo(expected);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void ExplicitSelections_IgnoreTheCulture(string culture)
    {
        Check.That(StringTable.ResolveLanguage(UiLanguage.German, CultureInfo.GetCultureInfo(culture)))
            .IsEqualTo(UiLanguage.German);
        Check.That(StringTable.ResolveLanguage(UiLanguage.English, CultureInfo.GetCultureInfo(culture)))
            .IsEqualTo(UiLanguage.English);
    }

    [Fact]
    public void NullCulture_Throws()
    {
        Check.ThatCode(() => StringTable.ResolveLanguage(UiLanguage.System, null!))
            .Throws<ArgumentNullException>();
    }
}
