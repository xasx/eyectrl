namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The production startup load (holistic review F2, mutant M1). The shipped-German gates build
/// their table through <see cref="ShippedGerman.Load"/>, their own copy of the App's call, so
/// nothing tied <c>App.xaml.cs</c> to them: passing <c>null</c> for the folder kept the shipped App
/// on English for good with 504 localization tests green. These tests go through
/// <see cref="StartupStringTable"/>, the one call <c>App.xaml.cs</c> makes.
/// </summary>
public class StartupStringTableTests
{
    private static EyeCtrlSettings Settings(UiLanguage language)
    {
        var settings = new EyeCtrlSettings();
        settings.General.UiLanguage = language;
        return settings;
    }

    /// <summary>Runs <paramref name="action"/> with the thread's UI culture set, then restores it.</summary>
    private static void WithUiCulture(string culture, Action action)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static void AssertGermanOnEverySurface(StringTable table, List<string> warnings)
    {
        Check.WithCustomMessage("Loading the shipped German warned:\n  " + string.Join("\n  ", warnings))
            .That(warnings).IsEmpty();
        Check.That(table.Language).IsEqualTo(UiLanguage.German);
        foreach (var surface in ShippedGerman.SwitchedOn)
        {
            Check.WithCustomMessage($"{surface} does not resolve German through the startup load.")
                .That(((CultureInfo)table.FormatProviderFor(surface)).Name).IsEqualTo("de-DE");
        }

        Check.That(table.T("startup-error.title")).IsEqualTo("EyeCtrl konnte nicht starten");
        Check.That(table.T("settings.tab.general.label")).IsEqualTo("Allgemein");
        Check.That(table.T("overlays.ring.leftClick")).IsEqualTo("Linksklick");
    }

    [Fact]
    public void TheShippedDefault_System_OnDeDe_GivesGermanOnAllElevenSurfaces()
    {
        // The explicit overload: the shipped files beside the test exe, de-DE, "system".
        var warnings = new List<string>();
        var table = StartupStringTable.Load(
            Settings(UiLanguage.System), ShippedGerman.GermanCulture, AppContext.BaseDirectory, warnings.Add);

        Check.That(ShippedGerman.SwitchedOn).HasSize(11);
        AssertGermanOnEverySurface(table, warnings);
    }

    [Fact]
    public void TheAppsOwnCall_ReadsTheProcessCultureAndTheFolderBesideTheExe()
    {
        // The overload App.xaml.cs calls: no folder and no culture are passed, so a wrong folder
        // or culture inside it (mutant M1, a null folder) leaves English and fails here.
        var warnings = new List<string>();
        StringTable? table = null;
        WithUiCulture("de-DE", () => table = StartupStringTable.Load(Settings(UiLanguage.System), warnings.Add));

        AssertGermanOnEverySurface(table!, warnings);
    }

    [Theory]
    [InlineData("en-US", UiLanguage.System)]
    [InlineData("fr-FR", UiLanguage.System)]
    [InlineData("de-DE", UiLanguage.English)]
    public void ThePersistedSelection_AndTheCulture_DecideEnglish(string culture, UiLanguage selection)
    {
        var warnings = new List<string>();
        StringTable? table = null;
        WithUiCulture(culture, () => table = StartupStringTable.Load(Settings(selection), warnings.Add));

        Check.That(warnings).IsEmpty();
        Check.That(table!.Language).IsEqualTo(UiLanguage.English);
        Check.That(table.T("startup-error.title")).IsEqualTo("EyeCtrl — Startup Error");
    }

    [Fact]
    public void AnExplicitGerman_OnAnEnglishMachine_IsGerman()
    {
        var warnings = new List<string>();
        StringTable? table = null;
        WithUiCulture("en-US", () => table = StartupStringTable.Load(Settings(UiLanguage.German), warnings.Add));

        AssertGermanOnEverySurface(table!, warnings);
    }

    /// <summary>
    /// The call site, by source: <c>App.xaml.cs</c> activates what <see cref="StartupStringTable"/>
    /// builds (the one Activate in src/, pinned in <c>LanguageChangeTests</c>). Going back to a
    /// private copy of the load would leave every test above green.
    /// </summary>
    [Fact]
    public void TheAppActivates_WhatTheStartupSeamLoads()
    {
        var text = LocalizationTestData.SourceFiles()
            .Single(file => file.Relative == "src/EyeCtrl.App/App.xaml.cs").Text;

        Check.WithCustomMessage(
                "App.xaml.cs must call StringTable.Activate(StartupStringTable.Load(settings, ...)) so "
                + "the production load is the tested one.")
            .That(Regex.IsMatch(text, @"StringTable\s*\.\s*Activate\s*\(\s*StartupStringTable\s*\.\s*Load\s*\(\s*settings\s*,"))
            .IsTrue();
        Check.WithCustomMessage("App.xaml.cs loads a string table on its own; the seam owns that call.")
            .That(Regex.IsMatch(text, @"(?<![A-Za-z0-9_])StringTable\s*\.\s*Load\s*\(")).IsFalse();
    }
}
