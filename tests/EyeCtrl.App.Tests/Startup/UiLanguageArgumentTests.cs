namespace EyeCtrl.App.Tests.Startup;

using System;
using System.IO;
using EyeCtrl.App.Startup;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The <c>--ui-language system|de|en</c> flag (T04): a persisting setter. The choice is saved
/// through the App writer, an unknown or valueless token is ignored with a log line, and a failed
/// save never escapes into startup. The "applied after the single-instance guard" ordering is a
/// property of where <c>App.OnStartup</c> calls this (see <c>UiLanguageArgumentWiringTests</c>),
/// because a duplicate process exits before it can reach the write.
/// </summary>
public class UiLanguageArgumentTests
{
    private static EyeCtrlSettings SettingsWith(UiLanguage language)
    {
        var settings = new EyeCtrlSettings();
        settings.General.UiLanguage = language;
        return settings;
    }

    [Fact]
    public void FlagAbsent_AppliesNothingAndSaysNothing()
    {
        var settings = SettingsWith(UiLanguage.System);
        var saves = 0;

        var outcome = UiLanguageArgument.Apply(["--test-mode"], settings, () => saves++);

        Check.That(outcome).IsNull();
        Check.That(saves).IsEqualTo(0);
        Check.That(settings.General.UiLanguage).IsEqualTo(UiLanguage.System);
    }

    [Theory]
    [InlineData("de", UiLanguage.German)]
    [InlineData("en", UiLanguage.English)]
    [InlineData("system", UiLanguage.System)]
    public void KnownToken_IsSaved(string token, UiLanguage expected)
    {
        var settings = SettingsWith(UiLanguage.English);
        var saves = 0;

        var outcome = UiLanguageArgument.Apply(["--test-mode", "--ui-language", token], settings, () => saves++);

        Check.That(settings.General.UiLanguage).IsEqualTo(expected);
        Check.That(saves).IsEqualTo(1);
        Check.That(outcome).Contains("applied");
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("german")]
    [InlineData("")]
    public void UnknownToken_IsIgnoredWithALogLine(string token)
    {
        var settings = SettingsWith(UiLanguage.System);
        var saves = 0;

        var outcome = UiLanguageArgument.Apply(["--ui-language", token], settings, () => saves++);

        Check.That(outcome).Contains("ignored");
        Check.That(saves).IsEqualTo(0);
        Check.That(settings.General.UiLanguage).IsEqualTo(UiLanguage.System);
    }

    [Fact]
    public void ValuelessFlag_IsIgnored_NotACrash()
    {
        var settings = SettingsWith(UiLanguage.System);
        var saves = 0;

        var outcome = UiLanguageArgument.Apply(["--ui-language"], settings, () => saves++);

        Check.That(outcome).IsNotNull();
        Check.That(outcome).Contains("ignored");
        Check.That(saves).IsEqualTo(0);
    }

    [Fact]
    public void FlagAndValue_AreCaseInsensitive()
    {
        var settings = SettingsWith(UiLanguage.System);

        var outcome = UiLanguageArgument.Apply(["--UI-LANGUAGE", "DE"], settings, () => { });

        Check.That(outcome).Contains("applied");
        Check.That(settings.General.UiLanguage).IsEqualTo(UiLanguage.German);
    }

    [Fact]
    public void FirstOccurrenceWins()
    {
        var settings = SettingsWith(UiLanguage.System);

        UiLanguageArgument.Apply(["--ui-language", "de", "--ui-language", "en"], settings, () => { });

        Check.That(settings.General.UiLanguage).IsEqualTo(UiLanguage.German);
    }

    [Fact]
    public void FailedSave_DoesNotThrow_AndSaysSo()
    {
        // Save() validates the whole file and throws; the flag is a convenience and must not
        // take the boot down with it. The in-memory choice still applies (the table loads from
        // these settings), but the outcome names the persistence failure so a later launch
        // reverting is explained.
        var settings = SettingsWith(UiLanguage.System);

        var outcome = UiLanguageArgument.Apply(
            ["--ui-language", "de"], settings, () => throw new InvalidOperationException("read-only"));

        Check.That(outcome).Contains("not saved");
        Check.That(outcome).Contains("read-only");
        Check.That(settings.General.UiLanguage).IsEqualTo(UiLanguage.German);
    }

    [Fact]
    public void TryGetValue_ReturnsTheTokenAfterTheFlag_AndNullWhenLast()
    {
        Check.That(UiLanguageArgument.TryGetValue(["--ui-language", "de"], out var value)).IsTrue();
        Check.That(value).IsEqualTo("de");

        Check.That(UiLanguageArgument.TryGetValue(["--ui-language"], out var missing)).IsTrue();
        Check.That(missing).IsNull();

        Check.That(UiLanguageArgument.TryGetValue(["--test-mode"], out var absent)).IsFalse();
        Check.That(absent).IsNull();
    }
}

/// <summary>
/// The ordering half of the <c>--ui-language</c> contract that no unit test can execute: the flag
/// is applied inside <c>InitializeSettingsAndLogging</c>, and that method is called only AFTER the
/// single-instance guard in <c>OnStartup</c> — so a duplicate that loses the mutex exits before it
/// can write. Source-pinned for the same reason the other startup-ordering guards are: the real
/// path constructs the App.
/// </summary>
public class UiLanguageArgumentWiringTests
{
    [Fact]
    public void TheFlag_IsAppliedInsideInitializeSettingsAndLogging_AfterTheSingleInstanceGuard()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "App.xaml.cs"));

        var acquire = source.IndexOf("_singleInstance.TryAcquire", StringComparison.Ordinal);
        var initialize = source.IndexOf("InitializeSettingsAndLogging(e.Args)", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "App.OnStartup no longer calls InitializeSettingsAndLogging(e.Args) after TryAcquire, "
                + "so a duplicate instance could apply --ui-language before losing the mutex — it "
                + "must exit first. Repoint this check, do not delete it.")
            .That(initialize).IsStrictlyGreaterThan(acquire);

        var methodStart = source.IndexOf(
            "private (EyeCtrlSettings Settings, string SettingsFilePath) InitializeSettingsAndLogging",
            StringComparison.Ordinal);
        Check.That(methodStart).IsStrictlyGreaterThan(-1);
        var apply = source.IndexOf("UiLanguageArgument.Apply(", methodStart, StringComparison.Ordinal);
        Check.WithCustomMessage(
                "InitializeSettingsAndLogging no longer applies --ui-language through "
                + "tempSettingsService (the App writer). The DI SettingsService does not exist yet "
                + "at that point, so applying elsewhere would either not persist or write with the "
                + "wrong writer.")
            .That(apply).IsStrictlyGreaterThan(methodStart);
        var applyWindow = source[apply..Math.Min(source.Length, apply + 400)];
        Check.WithCustomMessage(
                "The --ui-language apply is not routed through tempSettingsService.Save(settings), "
                + "so the choice would not persist through the App writer.")
            .That(applyWindow).Contains("tempSettingsService.Save(settings)");
    }
}
