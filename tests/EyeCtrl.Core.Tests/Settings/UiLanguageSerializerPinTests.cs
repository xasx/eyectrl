namespace EyeCtrl.Core.Tests.Settings;

using System;
using System.IO;
using System.Text.Json.Nodes;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Pins the serializer's JSON token for <see cref="GeneralSettings.UiLanguage"/> against
/// <see cref="UiLanguageTokens"/> — the token the Supervisor's raw reader parses. The settings
/// writer spells enums by its own <c>JsonStringEnumConverter(CamelCase)</c> policy; if an enum
/// member is ever renamed, the two spellings drift and the Supervisor would read null forever.
/// Saves through a GUID-suffixed writer mutex so no test touches (or contends with) the
/// production settings file.
/// </summary>
public class UiLanguageSerializerPinTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "eyectrl_uilang_" + Guid.NewGuid().ToString("N") + ".json");

    private static string TestMutexName() =>
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData(UiLanguage.System)]
    [InlineData(UiLanguage.German)]
    [InlineData(UiLanguage.English)]
    public void SavedToken_MatchesTheUiLanguageTokensMapping(UiLanguage language)
    {
        var path = TempPath();
        try
        {
            var settings = new EyeCtrlSettings();
            settings.General.UiLanguage = language;
            new SettingsService(path, SettingsWriter.App, TestMutexName()).Save(settings);

            var raw = JsonNode.Parse(File.ReadAllText(path))!
                .AsObject()["general"]!.AsObject()["uiLanguage"]!.GetValue<string>();

            Check.That(raw).IsEqualTo(UiLanguageTokens.ToJson(language));
            Check.That(UiLanguageTokens.TryFromJson(raw, out var parsed)).IsTrue();
            Check.That(parsed).IsEqualTo(language);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
