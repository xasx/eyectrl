namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// "A language change rebuilds no visible surface" (synthesis Decision 6) — the switch happens at
/// restart, so the change path may not re-activate the table or alter any rendered text. Plus the
/// migration case: an existing <c>settings.json</c> without a language loads as <c>System</c> and
/// resolves German on a de-DE machine.
/// </summary>
public class LanguageChangeTests
{
    [Fact]
    public void ChoosingALanguage_LeavesTheRunningTable_AndEveryOtherRenderedLabelUntouched()
    {
        var before = StringTable.Current;
        Check.That(before).IsSameReferenceAs(StringTable.EmbeddedEnglish);

        var fixture = LanguageTestSupport.Build();
        var labelsBefore = fixture.Rows()
            .Where(r => r is not LabelSpec)
            .Select(r => r.Label)
            .ToList();

        fixture.LanguageRow().Set("german");

        Check.WithCustomMessage(
                "A language choice re-activated the process table, so the running UI switched "
                + "language before a restart — the restart-to-apply invariant is broken.")
            .That(StringTable.Current).IsSameReferenceAs(before);
        Check.That(StringTable.Current.Language).IsEqualTo(UiLanguage.English);
        Check.That(StringTable.Current.T("language-recovery.prompt.restart")).IsEqualTo("Restart now");

        // The pending note is the one sanctioned addition; every other row's text is byte-identical.
        var labelsAfter = fixture.Rows()
            .Where(r => r is not LabelSpec)
            .Select(r => r.Label)
            .ToList();
        Check.WithCustomMessage("A language change rewrote rendered settings labels before the restart.")
            .That(labelsAfter).ContainsExactly(labelsBefore);
    }

    [Fact]
    public void TheOnlyTableActivationInSource_IsTheStartupLoad()
    {
        // The restart-to-apply model, structurally: exactly one StringTable.Activate call in src/,
        // in the startup path. Any second activation is a live switch by another name — and the
        // settings window rebuilds under a gaze that may be mid-dwell.
        var activations = LocalizationTestData.SourceFiles()
            .Where(file => file.Text.Contains("StringTable.Activate(", StringComparison.Ordinal))
            .Select(file => file.Relative)
            .ToList();

        Check.WithCustomMessage(
                "Exactly one source file may call StringTable.Activate (the startup load in "
                + "App.xaml.cs). A second call site is a live language switch — the model is "
                + "restart-to-apply because re-laying out Settings under a live dwell is unsafe. "
                + "Repoint this guard at the new single activation, do not delete it.\n  "
                + string.Join("\n  ", activations))
            .That(activations).ContainsExactly("src/EyeCtrl.App/App.xaml.cs");
    }

    [Fact]
    public void AnExistingSettingsFileWithoutALanguage_LoadsSystem_AndResolvesGermanOnDeDe()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eyectrl_t04_migrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            // A file as an install predating T01 wrote it: no uiLanguage anywhere.
            File.WriteAllText(path, """{"theme":{"name":"Ember"}}""");

            var settings = new SettingsService(path).Load();
            Check.That(settings.General.UiLanguage).IsEqualTo(UiLanguage.System);

            var table = StringTable.Load(
                settings.General.UiLanguage, CultureInfo.GetCultureInfo("de-DE"), looseFileDirectory: null);
            Check.That(table.Language).IsEqualTo(UiLanguage.German);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }
}
